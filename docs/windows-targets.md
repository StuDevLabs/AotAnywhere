# Windows targets

How AotAnywhere cross-compiles to `win-x64` / `win-arm64` from Linux and macOS
hosts.

On a Linux or macOS host, `dotnet publish -r win-x64` (or `win-arm64`) normally
stops at the `link.exe` step, since there is no MSVC. The package bridges that
with the `AotAnywhereWindowsLink` MSBuild task, which translates the MSVC-style
link arguments the ILC targets produce and drives `zig cc -target
<arch>-windows-gnu`, which links with lld against the MinGW-w64 (UCRT) import
libraries zig bundles. The MSVC-built NativeAOT runtime libraries link against
the MinGW C runtime through a small glue object the task compiles in (MSVC `/GS`
stack-cookie helpers, MSVC-mangled `operator new`/`delete`, the arm64
`_Interlocked*` out-of-line helpers, and a few marker symbols).

Things to know:

- The output imports the Universal CRT (`api-ms-win-crt-*`), exactly like an
  MSVC-linked NativeAOT binary, so it runs on any stock Windows 10+ system with
  no extra runtime.
- A `.pdb` is produced next to the binary and copied to the publish directory,
  as on Windows.
- `/MERGE` is honored for plain sections — the task renames them in copies of
  the input objects so lld produces the same merged layout as link.exe. The one
  exception is `$`-grouped sections, most visibly ILC's
  `/MERGE:.managedcode=.text` on net10.0 and later: link.exe keeps a merged
  group in `$`-suffix order, lld does not once the group shares an output
  section with other contributors, and the reordering pulls the bootstrapper's
  `.managedcode$A`/`$Z` brackets off the managed code — leaving a managed-code
  range a few bytes wide, which fail-fasts the first GC or exception. Those
  sections are left unmerged, keeping `.managedcode` as its own section (the
  shape .NET 8 emits anyway); the image comes out the same size either way.
- The `/GS` stack cookie is randomized at startup, mirroring MSVC's
  `__security_init_cookie`.
- `/OPT:REF` and `/OPT:ICF` (dead-code stripping and identical-code folding)
  are honored with a second link pass — zig cc cannot pass COFF `/OPT` flags
  through, so the task replays the underlying lld-link invocation with the
  flags appended, bringing the size in line with an MSVC link. Paths containing
  spaces are routed through temporary symlink aliases for the pass; the one
  shape that cannot be fixed up is a space in the output *file name* itself
  (renaming after the link would desync the PDB reference embedded in the
  image), which fails the link with a clear error. Set
  `AotAnywhereWindowsLinkOptimize=false` to skip the pass and link without
  `/OPT` (roughly 15% larger output).
- Some MSVC hardening/link features are still not carried over: Control Flow
  Guard and CET shadow-stack markers (`/CETCOMPAT`) are not emitted. For
  maximum-hardening release builds, link on Windows with MSVC.
- On a Windows host the package does nothing for `win-*` RIDs; the SDK's native
  MSVC link (including cross-arch win-x64 ↔ win-arm64 with the right VS
  components) applies.
