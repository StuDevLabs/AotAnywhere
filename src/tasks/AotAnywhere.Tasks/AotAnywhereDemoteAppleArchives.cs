using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace AotAnywhere.Tasks;

/// Applies the SDK's exported-symbols list to the static libraries of a zig
/// macOS link (see MachoArchiveDemoter): each `.a` among Archives that has
/// something to demote is copied under OutputDir, demoted there, and its
/// link argument redirected to the copy. The originals - typically in the
/// shared NuGet cache - are never modified.
///
/// Copies keep their original file name (in a per-archive subdirectory):
/// zig 0.17's Mach-O linker orders link inputs by basename, so renaming an
/// archive could move it relative to the ILC object and its managed-code
/// bookends (see _MacManagedStartObj in DirectLink.targets).
public sealed class AotAnywhereDemoteAppleArchives : MSBuildTask
{
    /// The link line, in order (@(_MacLinkArg)).
    [Required] public ITaskItem[] LinkArgs { get; set; } = System.Array.Empty<ITaskItem>();

    /// Candidate static libraries (@(NativeLibrary)); non-archives are ignored.
    public ITaskItem[] Archives { get; set; } = System.Array.Empty<ITaskItem>();

    /// The SDK's $(ExportsFile); empty means export nothing.
    public string ExportsFile { get; set; } = "";

    /// Where the demoted copies go (an intermediate directory).
    [Required] public string OutputDir { get; set; } = "";

    /// LinkArgs with each demoted archive's argument pointing at its copy.
    [Output] public ITaskItem[] RewrittenLinkArgs { get; set; } = System.Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        var keep = ExportsFile.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : MachoExportsDemoter.ParseExportsList(File.ReadAllLines(ExportsFile));

        var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in Archives)
        {
            var path = Unquote(item.ItemSpec);
            if (!path.EndsWith(".a", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
                redirects.ContainsKey(Path.GetFullPath(path)))
                continue;

            try
            {
                var data = File.ReadAllBytes(path);
                if (!MachoArchiveDemoter.IsArchive(data))
                    continue;

                var demoted = MachoArchiveDemoter.Demote(data, keep);
                if (demoted == 0)
                {
                    Log.LogMessage(MessageImportance.Low, $"AotAnywhere: no external symbols to demote in '{path}'.");
                    continue;
                }

                var copyDir = Path.Combine(OutputDir, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(copyDir);
                var copy = Path.Combine(copyDir, Path.GetFileName(path));
                File.WriteAllBytes(copy, data);
                redirects[Path.GetFullPath(path)] = copy;
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: demoted {demoted} external symbols in '{path}' to private-external, linking '{copy}'.");
            }
            catch (Exception ex) when (ex is IOException or MachoFormatException)
            {
                Log.LogError($"AotAnywhere: failed to apply the exports list to '{path}': {ex.Message}");
                return false;
            }
        }

        RewrittenLinkArgs = LinkArgs.Select(arg =>
        {
            var spec = arg.ItemSpec;
            var path = Unquote(spec);
            if (path.Length == 0 || !Path.IsPathRooted(path) ||
                !redirects.TryGetValue(SafeFullPath(path), out var copy))
                return arg;
            var quoted = spec.Length != path.Length;
            return (ITaskItem)new TaskItem(quoted ? $"\"{copy}\"" : copy);
        }).ToArray();
        return !Log.HasLoggedErrors;
    }

    static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
    }

    static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
