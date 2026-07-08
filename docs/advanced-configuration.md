# Advanced configuration

Internals and knobs most users never need. For everyday use see the
[Quick start](../README.md#quick-start) section of the README.

## How linking works (no native toolchain, no shim)

AotAnywhere never invokes clang, ld, lld, llvm-objcopy or MSVC's `link.exe`, and
ships **no per-host native binary**. Every host operation is either done
directly by Zig or in managed code:

- **Linux and macOS links** are taken over in MSBuild (`DirectLink.targets`):
  the ILC SDK still computes everything that goes into the link, and the
  package's target reconstructs that command line, applies the small set of
  zig-specific fixups, and runs `zig cc` directly. The full zig command line is
  visible in build logs and binlogs.
- **Windows links** (from a non-Windows host) are done by the
  `AotAnywhereWindowsLink` MSBuild task: it translates the MSVC `link.exe`
  arguments the SDK produces into a `zig cc -target <arch>-windows-gnu` MinGW
  cross-link, honours `/MERGE` by rewriting COFF sections, and supplies the
  MSVC↔MinGW CRT glue and stub import libraries. On a Windows host, `win-*`
  targets link natively with MSVC and the package stays inert.
- **Symbol stripping** for Linux targets is done by the `AotAnywhereStrip`
  MSBuild task (Zig cannot strip ELF), which implements the minimal ELF surgery
  `llvm-objcopy` would do (strip non-alloc sections, `--only-keep-debug` sidecar,
  `.gnu_debuglink`).

The ILC SDK's linker/objcopy probes (`command -v`) that would otherwise require
clang/llvm-objcopy on `PATH` are simply pointed at the restored Zig, which
satisfies them without any clang behaviour.

The managed tasks ship as a single portable `AotAnywhere.Tasks.dll`
(netstandard2.0) under `build/tasks/` — one assembly that runs on every host,
with no compile-on-demand and no cross-compiled per-host binaries.

See [direct-link.md](direct-link.md) for the design.

## Using your own Zig

By default the package relies on Zig provided by the unofficial
[Vezel.Zig.Toolsets](https://github.com/vezel-dev/zig-toolsets) NuGet package.
You can select the version with the `ZigVersion` property.

If you don't want to use Zig from the Vezel.Zig.Toolsets NuGet package, you can
specify `/p:UseExternalZig=true`. This will use whatever Zig is on your PATH.
[Download](https://ziglang.org/download/) an archive with Zig for your host
machine, extract it and place it on your PATH.

> Maintainers: bumping the pinned `ZigVersion` follows the
> [ZigVersion bump checklist](zig-version-bump.md).
