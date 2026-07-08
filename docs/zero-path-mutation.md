# Zero PATH mutation (spike)

This is the design writeup for ROADMAP item #6. It records where the
environment-mutation arc stands, what the single remaining PATH mutation is, and
why — after measuring the options on a real Windows runner — it is the terminal
state rather than something we can remove. The short version:

- **The last PATH mutation is a single, process-scoped `PATH` prepend that only
  happens on a Windows *host*.** Non-Windows hosts run with zero PATH mutation
  (`PointLinkerToZig` points the SDK probes at zig by absolute path). Linux and
  macOS *hosts* are done.
- **Removing it would need the linker resolvable by a colon-free name** that the
  SDK's `where /Q` linker probe accepts. **A Windows-runner experiment showed no
  such form exists** (see [The `where /Q` experiment](#the-where-q-experiment)):
  every colon-free path `where` was given failed to resolve, and it cannot take
  an absolute path because of the drive colon. The bare-name-on-`PATH` form is
  the only one that works, which is exactly what the prepend provides. So the
  prepend stays — this is the "no, because X" outcome the ROADMAP anticipated.
- **The process-env channels are gone.** Earlier iterations passed zig's path
  and the Apple sysroot to the native shim through `AOTANYWHERE_ZIG` /
  `AOTANYWHERE_APPLE_SYSROOT` env vars. The shim is gone, so `AOTANYWHERE_ZIG`
  was deleted and the macOS takeover reads the sysroot from an MSBuild property
  (`AOTANYWHERE_APPLE_SYSROOT` survives only as an optional *user-supplied*
  override). There are no package-set process-env channels left to collapse.

See also the "Design constraints discovered" and "Not covered" sections of
[`direct-link.md`](direct-link.md), which this document extends.

## Current state

The build no longer needs anything on `PATH` except in one case. Walking the
mutations that used to exist and where they went:

| Channel | Was | Now |
| --- | --- | --- |
| Native shim directory on `PATH` | prepended on every host | **gone** — there is no shim. |
| `zig` on `PATH` | prepended on every host | **gone off Windows.** `SetPathToZig` resolves zig's absolute path (`$(_AotAnywhereZigExe)`); the DirectLink takeovers run it directly and `PointLinkerToZig` sets `CppLinker`/`ObjCopyName` to that absolute path so the SDK probes resolve without `PATH`. Still prepended (zig's toolset dir) on a **Windows host** — see below. |
| `AOTANYWHERE_ZIG` | shim read it to exec zig | **removed** (no shim). |
| `AOTANYWHERE_APPLE_SYSROOT` | shim read it for the Apple stubs | now only an optional *user* override (read as an MSBuild property); the macOS takeover uses `$(AotAnywhereAppleSysroot)`. |

So the only remaining mutation is a single Windows-host `PATH` prepend.

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
absolute path, so off Windows we point `CppLinker`/`ObjCopyName` straight at
zig's absolute path and skip PATH entirely. `where /Q "<abs path>"` does **not**:
it reads the drive-letter colon in `C:\…\zig.exe` as its own `path:pattern`
delimiter and fails with `Invalid pattern is specified in "path:pattern"`. So on
a Windows host the linker has to be found by a bare name (`zig`) with zig's
toolset directory prepended to `$PATH`. (The link/strip themselves never invoke
that `zig` — every link is a DirectLink takeover or the Windows task, every strip
is the managed `AotAnywhereStrip` — so the prepend exists purely to satisfy the
probe.)

The probe `Exec` is unconditional and runs with no `WorkingDirectory`, and the
value it probes (`$(CppLinker)`) is the same value the SDK would later exec — so
we cannot skip the probe, cannot give it a probe-only value that differs from the
exec path, and cannot rely on a particular working directory.

## The `where /Q` experiment

The one open question was whether *any* colon-free form of the linker path would
satisfy `where /Q`. Measured directly on `windows-latest` through an MSBuild
`<Exec>` (the same task the SDK uses), against a real file. Exit `0` = `where`
resolved it. (The probe was run against a `clang.exe` at the time, but the
finding is about `where`'s path handling and applies identically to a bare
`zig`.)

| # | Form probed | Exit | Verdict |
| --- | --- | --- | --- |
| 0 | bare filename, dir on `PATH` | **0** | today's approach — works |
| 1 | `C:\…\tool.exe` (drive colon) | 2 | baseline failure (`path:pattern` misparse) |
| 2 | `\…\tool.exe` (drive-relative, colon-free) | 1 | not found |
| 3 | `C:/…/tool.exe` (forward slashes) | 2 | colon still present — fails |
| 4 | `subdir\tool.exe` (relative), cwd = containing dir | 1 | not found |
| 5 | `subdir\tool.exe` (relative), cwd = elsewhere | 1 | not found |
| 6 | `\…\subdir:tool` (`where` `dir:pattern`, colon-free dir) | 1 | not found |

The finding is unambiguous: **`where` resolves a bare filename (via `PATH` or the
current directory) but not any path that carries a directory component**, colon
or not. Removing the colon does not help — forms 2, 4, 5 and 6 are all colon-free
and all fail. There is therefore no "colon-free linker name" that lets zig sit in
its own toolset directory and still be found without putting that directory on
`PATH`.

(The experiment lived in `eng/where-probe/` and `.github/workflows/where-probe.yml`
and was removed once it had answered the question; this table is its result.)

## Options considered

### A. Colon-free `CppLinker` path — rejected (measured)

Point `CppLinker`/`ObjCopyName` at zig by a colon-free path (drive-relative,
project-relative, or `where`'s `dir:pattern`). The experiment above shows `where`
rejects all of them: it will not resolve a value containing a directory
component. Dead end.

### B. `where "$ENVVAR:pattern"` — rejected

`where` accepts `where "$SOMEVAR:zig"` (search the `;`-list in env var
`SOMEVAR`). But `$(CppLinker)` is also the value the SDK would exec as the real
linker, and `"$SOMEVAR:zig" args` is not a runnable executable. Probe-name and
exec-path are the same property; they cannot diverge. (This is essentially a
namespaced `PATH` in an env var anyway — not less mutation, just spelled
differently.)

### C. Skip / pre-satisfy the probe — rejected

The probe `Exec` is unconditional and overwrites `_WhereLinker` with its own
result, so a pre-set value cannot survive, and there is no package hook to
condition the `Exec` out.

### D. Drop a bare-named zig into the current directory — rejected

`where` searches the current directory, so a bare `zig.exe` in the build's cwd
resolves with no `PATH` mutation — but the probe's cwd is the consumer's launch
directory (the `Exec` sets no `WorkingDirectory`), not ours, and writing a build
artifact there would be worse than a scoped, process-only prepend of zig's
toolset directory anyway.

### E. Accept the prepend as terminal — the outcome

The current state *is* the terminal one: a single, process-scoped prepend of
zig's toolset directory, on Windows hosts only, gated behind the SDK's own
`where /Q` limitation. It mutates nothing persistent and nothing outside the
build process. Short of the SDK gaining a way to pass the linker by absolute path
on Windows (an upstream ask — see ROADMAP #7), this is as far as the arc goes.

## Conclusion

The zero-PATH-mutation arc is complete to the extent the tooling allows: every
host but Windows runs with no PATH mutation, no package-set env channels remain,
and the Windows-host prepend is irreducible given the SDK's `where /Q` probe
(proven by experiment, not assumption). The only routes to removing it are
upstream: a dotnet/runtime hook to pass the Windows linker by absolute path, or
to skip the probe — which folds into the broader "override the link invocation"
ask in ROADMAP #7.
