using System.Diagnostics;
using System.Text;
using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace AotAnywhere.Tasks;

/// Links a NativeAOT Windows target from a non-Windows host by translating the
/// MSVC link.exe arguments to `zig cc -target <arch>-windows-gnu` and running
/// zig. Replaces the link.exe-impersonating personality of link_shim.zig: the
/// package's targets invoke this directly (BeforeTargets="LinkNative") instead
/// of routing $(CppLinker) through a PATH shim.
///
/// Pure logic lives in MsvcArgTranslator (option translation) and
/// CoffSectionRenamer (/MERGE); this task is the orchestration - support-file
/// emission, /MERGE object copies, process launch, and MSBuild logging.
public sealed class AotAnywhereWindowsLink : MSBuildTask
{
    /// The MSVC-style link tokens: the object, /OUT, /DEF etc. framing the SDK
    /// would have written to link.rsp, plus @(LinkerArg) (which carries the
    /// injected --target=<triple>).
    [Required] public ITaskItem[] MsvcArgs { get; set; } = System.Array.Empty<ITaskItem>();

    /// Absolute path to zig (or bare "zig"); $(_AotAnywhereZigExe).
    [Required] public string ZigExe { get; set; } = "";

    /// Directory for the stub archives and /MERGE object copies (the native
    /// intermediate output path).
    [Required] public string SupportDir { get; set; } = "";

    /// Path to the shipped MSVC glue source, compiled into the link.
    [Required] public string GlueSource { get; set; } = "";

    static readonly string[] StubLibNames = { "libLIBCMT.a", "libOLDNAMES.a", "liblibcpmt.a", "libuuid.a" };
    const string StubDirName = "aotanywhere-msvc-stub-libs";

    public override bool Execute()
    {
        // Each item is an rsp-style line (quoted, possibly several tokens), not
        // a bare argument - tokenize exactly as the shim tokenizes link.rsp.
        var tokens = MsvcArgTranslator.Tokenize(MsvcArgs.Select(i => i.ItemSpec));
        var translation = MsvcArgTranslator.Translate(tokens);

        foreach (var warning in translation.Warnings)
            Log.LogWarning(warning);
        if (!translation.SawTarget)
            Log.LogWarning("AotAnywhere: no --target=<triple> was injected; linking for the host by mistake is likely.");

        Directory.CreateDirectory(SupportDir);
        ApplyMerges(translation);
        var stubDir = WriteStubArchives();

        var argv = new List<string> { "cc" };
        argv.AddRange(translation.Args);
        argv.Add(GlueSource);
        argv.Add("-L" + stubDir);

        return RunZig(argv) && !Log.HasLoggedErrors;
    }

    /// Honors /MERGE by renaming COFF sections in the input objects. Inputs are
    /// never modified in place (some live in the shared NuGet cache), so any
    /// object that actually gets a rename is copied into SupportDir and its
    /// linker argument redirected to the copy. Port of link_shim.zig applyMerges.
    void ApplyMerges(MsvcTranslation t)
    {
        if (t.Merges.Count == 0) return;

        for (var index = 0; index < t.ObjectInputs.Count; index++)
        {
            var path = t.ObjectInputs[index];
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception e) { Log.LogWarning($"AotAnywhere /MERGE: skipping '{path}': {e.Message}"); continue; }

            var total = new RenameResult();
            foreach (var (from, to) in t.Merges)
            {
                var r = CoffSectionRenamer.RenameSections(data, from, to);
                total.Renamed += r.Renamed;
                total.SkippedLong += r.SkippedLong;
                total.SkippedInitialized += r.SkippedInitialized;
            }

            if (total.SkippedLong > 0)
                Log.LogWarning($"AotAnywhere /MERGE: {total.SkippedLong} section(s) in '{path}' not merged (target name over 8 chars).");
            if (total.SkippedInitialized > 0)
                Log.LogWarning($"AotAnywhere /MERGE: {total.SkippedInitialized} initialized section(s) in '{path}' not merged into .bss.");
            if (total.Renamed == 0) continue;

            // The index prefix keeps same-named objects from different dirs apart.
            var copyPath = Path.Combine(SupportDir, $"aotanywhere-merged-{index}-{Path.GetFileName(path)}");
            try { WriteReplacing(copyPath, data); }
            catch (Exception e) { Log.LogWarning($"AotAnywhere /MERGE: could not write merged copy of '{path}': {e.Message}"); continue; }

            for (var i = 0; i < t.Args.Count; i++)
                if (t.Args[i] == path) t.Args[i] = copyPath;
            Log.LogMessage(MessageImportance.Normal,
                $"AotAnywhere /MERGE: renamed {total.Renamed} section(s); linking {copyPath} in place of {path}.");
        }
    }

    /// Writes via a temp file + rename so an interrupted run never leaves a
    /// truncated object for an incremental build to pick up.
    static void WriteReplacing(string path, byte[] data)
    {
        var tmp = path + ".aotanywhere-tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    /// The MSVC objects carry /DEFAULTLIB directives for MSVC-only libraries;
    /// empty archives satisfy the directive while the MinGW CRT and the glue
    /// provide the symbols. Returns the stub directory to add as -L.
    string WriteStubArchives()
    {
        var stubDir = Path.Combine(SupportDir, StubDirName);
        Directory.CreateDirectory(stubDir);
        foreach (var name in StubLibNames)
            File.WriteAllText(Path.Combine(stubDir, name), "!<arch>\n");
        return stubDir;
    }

    bool RunZig(List<string> argv)
    {
        var sb = new StringBuilder();
        foreach (var arg in argv) AppendArgument(sb, arg);
        var arguments = sb.ToString();

        Log.LogMessage(MessageImportance.Normal, $"AotAnywhere: {ZigExe} {arguments}");

        var psi = new ProcessStartInfo
        {
            FileName = ZigExe,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log.LogMessage(MessageImportance.Normal, e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.LogMessage(MessageImportance.High, e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Log.LogError($"AotAnywhere: zig cc failed with exit code {p.ExitCode}.");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Log.LogError($"AotAnywhere: could not run zig ('{ZigExe}'): {e.Message}");
            return false;
        }
    }

    /// Quotes a single argument for ProcessStartInfo.Arguments the way the CRT
    /// (and .NET's own PasteArguments) expects, so args with spaces survive.
    static void AppendArgument(StringBuilder sb, string arg)
    {
        if (sb.Length != 0) sb.Append(' ');
        if (arg.Length != 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        var idx = 0;
        while (idx < arg.Length)
        {
            var c = arg[idx++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (idx < arg.Length && arg[idx] == '\\') { idx++; backslashes++; }
                if (idx == arg.Length) sb.Append('\\', backslashes * 2);
                else if (arg[idx] == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); idx++; }
                else sb.Append('\\', backslashes);
            }
            else if (c == '"') { sb.Append('\\'); sb.Append('"'); }
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
