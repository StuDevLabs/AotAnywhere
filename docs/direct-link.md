# Direct link

`src/DirectLink.targets` links **Linux and macOS targets** by taking the link
over in MSBuild and running `zig cc` directly, instead of letting the ILC SDK
invoke a linker on `PATH`. Windows targets are handled the same way in spirit
but by a managed task (`AotAnywhereWindowsLink`), because the MSVC→MinGW
translation and `/MERGE` COFF surgery need real imperative code. There is **no
native shim** any more — no `clang`/`llvm-objcopy`/`link` stand-in binary.

This document began as the Linux prototype's design notes; the constraints and
findings below still describe how and why the flow works.

## What it does

The ILC SDK targets still compute everything that goes *into* the link; the
package only takes over *how* the link is invoked. The division of labor:

- **SDK targets (unchanged, source of truth for _what_ to link):**
  `SetupOSSpecificProps` computes `$(NativeObject)`, `@(NativeLibrary)` and
  `@(LinkerArg)` — the runtime/framework static libs, system libs, hardening
  flags — including the `--target` triple injected by `OverwriteTargetTriple`.
- **`AotAnywhereDirectLinkNative` / `…MacNative` (own _how_ it is linked):**
  reconstruct the command line the SDK's `LinkNative` would build (for a
  non-Apple Unix target, or an Apple target), apply the small set of
  zig-specific fixups (Linux: drop `-lz`, `-pie -Wl,-pie`, the shared-library
  null entry point; add `-Wl,-u,__Module`. macOS: sysroot stub `-F`/`-L`, drop
  `-ld_classic`, Swift overlay libs, the GS-cookie pad), and `Exec`
  `$(_AotAnywhereZigExe) cc …` directly.
- **`AotAnywhereStrip` task** performs the Linux symbol strip (ELF surgery zig
  cannot do — `--strip-unneeded`, the `--only-keep-debug` sidecar, and
  `.gnu_debuglink`), in managed code.

Windows targets go through the `AotAnywhereWindowsLink` task instead (see
[Advanced configuration](advanced-configuration.md)).

## Design constraints discovered

- **`LinkNative` cannot be redefined by the package.** For real NuGet
  consumers our `.targets` are imported through the project-extensions hook
  at the top of `Microsoft.Common.targets`, while the SDK imports
  `Microsoft.NETCore.Native.targets` much later. A later definition of a
  same-named target overrides an earlier one, so an override in our package
  would itself be overridden. Instead the takeover runs
  `BeforeTargets="LinkNative"` with LinkNative's own `Inputs`/`Outputs`; once it
  has produced `$(NativeBinary)`, LinkNative's incremental check finds its
  output up to date and skips itself. (The test harness imports `src/` targets
  *after* `Sdk.targets` — the opposite order — so only order-independent
  mechanisms like this one behave the same in both.)
- **The SDK's linker/objcopy probes are pointed at zig, not a shim.**
  `SetupOSSpecificProps` runs `command -v "$(CppLinker)"` (and the same for the
  objcopy strip tool; `where /Q` on Windows hosts) and errors when the tool is
  missing, before we ever run. Since every link is a takeover and every strip is
  the task, these tools are never actually invoked — they just have to resolve
  to an existing executable, so `PointLinkerToZig` points `CppLinker`/
  `ObjCopyName` at the restored zig. `command -v` accepts zig's absolute path;
  `where /Q` does **not** (it reads the drive-letter colon as its own
  `path:pattern` delimiter), so on a Windows host we use bare `zig` with its
  directory prepended to `PATH` — the one remaining PATH mutation, tracked in
  [`zero-path-mutation.md`](zero-path-mutation.md). Windows *targets* never reach
  this probe: win-cross links via the `AotAnywhereWindowsLink` task and a Windows
  host links win-* natively without importing the package.
- **The `-fuse-ld=lld` linker-version probe never fires.** The SDK only runs it
  for `LinkerFlavor=lld`; the glibc/musl/macOS flavors here leave it unset, so
  the probe (which *would* break on bare zig, since `zig` is not a clang driver)
  is skipped and `_LinkerVersion` stays unset. Module retention rides on
  `-Wl,-u,__Module` instead of the SDK's `sections.ld`.
- **Version drift lands in the drop list, not the structure.** net8 always adds
  `-lz` and spells the shared-library entry `-Wl,-e0x0`; net10 links the bundled
  `libz.a`/brotli instead and spells it `-Wl,-e,0x0`. Everything else (new
  static libs, zlib-ng, brotli) flows through `@(LinkerArg)` unchanged — which
  is the argument for keeping the SDK as the source of truth. The trade-off is
  that the reconstructed *framing* (which args the SDK wraps around
  `@(LinkerArg)`) can drift across pack versions and has to be re-checked on a
  floor bump; this bit us once on macOS (net11's `--gc-sections`/`--discard-all`
  are GNU-ld flags that corrupt a macho link).

## What was validated

Linux (glibc and musl, x64/arm64/armv7, net8/net9/net10) and macOS
(x64/arm64) publishes link this way in the CI matrix from every host, and the
`validate` job executes the resulting binaries. The Linux strip was proven
**byte-identical** to the retired `objcopy_shim.zig` on a real binary (stripped
image and `.dbg` sidecar both `cmp`-clean), so the managed `AotAnywhereStrip`
matches what llvm-objcopy produced. Shared libraries (`test/HelloLib`,
`NativeLib=Shared`) and a non-trivial `--selftest` app (real ICU, zlib and
OpenSSL at run time) are covered too; the shared-library path added
`-Wl,--undefined-version` to work around lld (16+) rejecting the undefined
`_init`/`_fini` in ILC's generated version script.

## Why this shape

- The full `zig cc` command line appears in build logs and binlogs — no argv
  rewriting hidden inside a spawned process.
- The link is constructed from structured MSBuild items, and the whole
  cross-toolchain surface is either MSBuild or one portable managed task
  assembly — no per-host native binary to build, ship or maintain.
- It removed the last reason the package needed a native shim, and got PATH
  mutation down to a single Windows-host case (see below).

## Not covered

- **`NativeLib=Static`** — the SDK archives with `ar` via `CppLibCreator`;
  untouched (the takeover conditions itself out and the SDK flow applies).
- **The Windows-host zig PATH prepend** — the last PATH mutation, needed because
  `where /Q` rejects an absolute path. Removing it would need a linker
  resolvable by a colon-free name; spiked in
  [`zero-path-mutation.md`](zero-path-mutation.md).
- **`StaticICULinking`/`StaticOpenSslLinking`** (consumer opt-in, not exercised
  here) drove `build-local.sh` with `CC=$(CppLinker)`. With the shim gone,
  `CppLinker` is bare zig, which is not a `zig cc` driver — so these would need
  a small `zig cc` wrapper reinstated if a consumer enables them.
