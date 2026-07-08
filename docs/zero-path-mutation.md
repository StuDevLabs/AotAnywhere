# Zero PATH mutation (spike)

This is the design writeup for ROADMAP item #6. It records where the
environment-mutation arc stands, what the single remaining PATH mutation is,
and which of the candidate fixes actually removes it. The short version:

- **The last PATH mutation is a single, process-scoped `PATH` prepend that only
  happens on a Windows *host*.** Non-Windows hosts already run with zero PATH
  mutation (`PointLinkerToShim` points the SDK probes at the shim by absolute
  path). Linux and macOS *hosts* are done.
- **Removing it cleanly needs the shim resolvable by a colon-free name** so the
  `where /Q` probe accepts it without a prepend. The only option that both
  satisfies the probe *and* survives being exec'd as the linker is a **relative
  path** — which likely works but rests on Windows `where.exe` semantics this
  spike could not run (the author is on macOS). It should be validated on the
  Windows leg of `cross-platform-validation.yml` before it is trusted.
- **The `AOTANYWHERE_ZIG` / `AOTANYWHERE_APPLE_SYSROOT` env channels can be
  collapsed** into `LinkerArg`-injected flags (the same channel the `--target`
  triple already rides), but the payoff is marginal — they are namespaced,
  process-scoped env vars, not PATH — and it adds parse-and-strip surface to the
  shim's link hot path. Recommend doing it only if bundled with the PATH change,
  not as standalone churn.

See also the "Design constraints discovered" and "Not covered" sections of
[`direct-link.md`](direct-link.md), which this document extends.

## Current state

The link no longer needs anything on `PATH` except in one case. Walking the
mutations that used to exist and where they went:

| Channel | Was | Now |
| --- | --- | --- |
| Shim directory on `PATH` | prepended on every host | **gone off Windows** (`CppLinker`/`ObjCopyName` set to the shim's absolute path; `command -v` accepts it). Still prepended on a **Windows host** — see below. |
| `zig` on `PATH` | prepended | gone everywhere. `SetPathToZig` resolves zig's absolute path; the direct link and shim compilation use `$(_AotAnywhereZigExe)`, and the shim receives it through `AOTANYWHERE_ZIG`. |
| `AOTANYWHERE_ZIG` | — | process-env var the `clang`/`link` shim personalities read to exec zig by absolute path (falls back to `zig` on `PATH`). |
| `AOTANYWHERE_APPLE_SYSROOT` | — | process-env var the `clang` personality reads for the Apple stub sysroot. |

So the remaining mutations are: one Windows-host `PATH` prepend, plus two
process-scoped, namespaced env vars.

## Why the Windows-host prepend is still there

On a Windows host targeting Linux/macOS, `SetupOSSpecificProps` (in the ILC
SDK's `Microsoft.NETCore.Native.Unix.targets`) probes the linker and the objcopy
symbol stripper before we ever run:

```xml
<_CommandProbe>command -v</_CommandProbe>
<_CommandProbe Condition="$([MSBuild]::IsOSPlatform('Windows'))">where /Q</_CommandProbe>
...
<Exec Command="$(_CommandProbe) &quot;$(CppLinker)&quot;" IgnoreExitCode="true" ... />
```

and `Error`s if the probe exits non-zero. `command -v "<abs path>"` accepts an
absolute path, so off Windows we point `CppLinker`/`ObjCopyName` straight at the
materialized shim and skip PATH entirely. `where /Q "<abs path>"` does **not**:
it reads the drive-letter colon in `C:\...\clang.exe` as its own `path:pattern`
delimiter and fails with `Invalid pattern is specified in "path:pattern"`. So on
a Windows host the shim has to be found by a bare name (`clang`,
`llvm-objcopy` — the SDK defaults) with its directory prepended to `$PATH`.

The probe `Exec` is unconditional and the value it probes (`$(CppLinker)`) is the
same value later exec'd as the real linker, so we cannot skip the probe and
cannot give it a probe-only value that differs from the exec path.

## Options considered

### A. Relative, colon-free `CppLinker` — recommended, pending validation

Point `CppLinker`/`ObjCopyName` at the shim by a path *relative* to the project
directory (the working directory of the probe and link `Exec`s), e.g.
`$(IntermediateOutputPath)aotanywhere\clang.exe`. It contains no drive colon, so
`where /Q` should not misparse it, and a relative path exec's fine with the
project dir as cwd.

Open questions that need a Windows box (the whole reason this stays a spike):

- **Does `where.exe` resolve a relative multi-segment path?** `where` splits a
  pattern containing a separator into `<dir>\<filename>` and searches `<dir>`;
  that directory is expected to resolve relative to cwd, but this was not
  verified here.
- **Is cwd guaranteed to be `$(MSBuildProjectDirectory)` for the probe `Exec`?**
  MSBuild's `Exec` defaults its working directory to the project directory, and
  the SDK probe sets no `WorkingDirectory`, so this should hold — but it is an
  assumption worth asserting in the test.
- **`$(IntermediateOutputPath)` can be absolute.** Artifacts-output layouts
  (`ArtifactsPath`) and explicit `BaseIntermediateOutputPath` overrides make it
  rooted again, reintroducing the colon. The change must therefore be **guarded**
  — use the relative form only when the shim path is not rooted
  (`[System.IO.Path]::IsPathRooted`), and fall back to the current prepend
  otherwise. In the common (default) layout the prepend disappears; in the
  rooted-output layout it stays. That is still a strict improvement, and it is
  the honest shape given the constraint.

Sketch (in `PointLinkerToShim`, replacing the unconditional `<PrependPath>`):

```xml
<PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
  <_AotAnywhereShimRelDir>$(IntermediateOutputPath)aotanywhere\</_AotAnywhereShimRelDir>
  <_AotAnywhereShimColonFree
    Condition="!$([System.IO.Path]::IsPathRooted('$(IntermediateOutputPath)'))">true</_AotAnywhereShimColonFree>
</PropertyGroup>
<!-- colon-free: point the probe at the relative path, no PATH mutation -->
<PropertyGroup Condition="... '$(_AotAnywhereShimColonFree)' == 'true'">
  <CppLinker>$(_AotAnywhereShimRelDir)clang.exe</CppLinker>
  <ObjCopyName Condition="$(RuntimeIdentifier.StartsWith('linux'))">$(_AotAnywhereShimRelDir)llvm-objcopy.exe</ObjCopyName>
</PropertyGroup>
<!-- fallback: rooted intermediate path, keep the prepend as today -->
<PrependPath Condition="... '$(_AotAnywhereShimColonFree)' != 'true'" Value="$(ClangShimDir)" />
```

### B. `where "$ENVVAR:pattern"` — rejected

`where` accepts `where "$SOMEVAR:clang"` (search the `;`-list in env var
`SOMEVAR` for `clang`) — colon-free in the sense that matters to the probe. But
`$(CppLinker)` is also the string exec'd as the real linker for macOS/Windows
targets, and `"$SOMEVAR:clang" args` is not a runnable executable. Probe-name and
exec-path are the same property; they cannot diverge. Rejected.

### C. Skip / pre-satisfy the probe — rejected

The probe `Exec` is unconditional and overwrites `_WhereLinker` with its own
result, so a pre-set value cannot survive, and there is no package hook to
condition the `Exec` out. Rejected.

### D. Drop a bare-named shim into the project directory — rejected

`where` (and cmd's command resolution) search the current directory, so a bare
`clang.exe` sitting in the project root resolves for both probe and exec with no
PATH mutation. But writing a build artifact into the consumer's project root is
worse than a scoped, process-only prepend of our own obj directory. Rejected.

### E. Accept the prepend as terminal — the fallback outcome

If A does not pan out on Windows, the honest conclusion is that the current state
*is* the terminal one: a single, process-scoped prepend of the shim's own
intermediate directory, on Windows hosts only, gated behind the SDK's own
`where /Q` limitation. It mutates nothing persistent and nothing outside the
build process. This is the "no, because `where /Q` cannot take an absolute path"
writeup the ROADMAP anticipated.

## Collapsing the env channels

Independently of the PATH question, `AOTANYWHERE_ZIG` and
`AOTANYWHERE_APPLE_SYSROOT` could move from process-env vars to flags injected
through `@(LinkerArg)` — the same channel `OverwriteTargetTriple` already uses
for `--target=<triple>`, which reaches **both** shim personalities that need it:

- The `clang` personality (macOS targets) gets `LinkerArg` items on its command
  line directly.
- The `link` personality (Windows targets) gets them too: `--target` is injected
  via `LinkerArg` and arrives inside the expanded `link.rsp` (see the
  `link_shim.zig` header). A `--aotanywhere-zig=<path>` line would arrive the
  same way.
- The `objcopy` personality needs neither var — it does its own ELF surgery and
  never execs zig — so it needs no channel at all.

Feasibility is therefore *not* the blocker (an earlier read of this suggested the
`link` rsp path was entangled; it is not — `LinkerArg` covers it). The blockers
are judgement calls:

- **Marginal benefit.** These are namespaced, process-scoped env vars, not
  PATH. `direct-link.md` already calls this trade "cleaner, but still not zero
  mutation."
- **Added risk on the link hot path.** Each shim personality would have to
  recognize the new flags and *strip* them before forwarding to `zig cc` (an
  unrecognized `--aotanywhere-*` flag reaching zig fails the link). That is new
  parse-and-strip surface in the most correctness-sensitive code in the repo.
- **Fallback still required.** The `zig`-on-PATH fallback (external zig,
  degraded restore) must survive, so the env var read cannot simply be deleted.

Recommendation: collapse the channels only if implementing option A anyway, so
the shim parsing changes and the Windows validation land together. Both the
`clang` (macOS target) and `link` (win-x64 target) paths are exercisable from a
Linux/macOS host, so this part is testable without a Windows host — unlike the
PATH prepend itself.

## What needs a Windows host to validate

- Option A's `where /Q "<relative>\clang.exe"` resolving with cwd =
  project directory, for a Linux target (clang probe) and with `StripSymbols`
  on (objcopy probe).
- That the relative `CppLinker`/`ObjCopyName` also *exec* correctly at link time
  from the same cwd.
- The rooted-`IntermediateOutputPath` fallback still prepends and still links.

The `build`/`validate` matrix in `cross-platform-validation.yml` already links
from a Windows host to Linux and macOS targets, so wiring a Windows-host leg with
a non-default (and a rooted) `IntermediateOutputPath` is the concrete next step.
