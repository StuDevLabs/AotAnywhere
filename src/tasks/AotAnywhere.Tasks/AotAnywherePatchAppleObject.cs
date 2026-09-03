using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace AotAnywhere.Tasks;

/// Prepares the ILC relocatable object for a zig macOS link:
///
///  - clears S_ATTR_DEBUG on __DATA,.dotnet_eh_table so zig's Mach-O linker
///    does not drop the managed GC-info table from the binary (which would
///    make the first garbage collection crash - see MachoEhTablePatcher);
///  - when ApplyExportsList is set, demotes every defined external symbol not
///    named in ExportsFile to private-external, which is how the SDK's
///    `-exported_symbols_list` (ignored by zig) takes effect - see
///    MachoExportsDemoter. An empty ExportsFile keeps nothing, matching the
///    `-exported_symbols_list /dev/null` the SDK passes for executables.
///
/// Runs in place on $(NativeObject), immediately before the link. Idempotent:
/// the file is rewritten only when something actually changed, so incremental
/// builds see a stable timestamp. (ILC regenerates the object and the exports
/// file together, so a stale demotion cannot outlive its list.)
public sealed class AotAnywherePatchAppleObject : MSBuildTask
{
    /// The ILC-produced Mach-O relocatable object ($(NativeObject)).
    [Required] public string ObjectFile { get; set; } = "";

    /// The SDK's $(ExportsFile): one exported symbol per line, as ILC writes
    /// it. Empty means export nothing (when ApplyExportsList is set).
    public string ExportsFile { get; set; } = "";

    /// Whether to apply the exports list at all. Off for a shared library
    /// with no ExportsFile, where the SDK passes no list and everything
    /// stays exported.
    public bool ApplyExportsList { get; set; }

    public override bool Execute()
    {
        try
        {
            var data = File.ReadAllBytes(ObjectFile);
            var changed = false;

            if (MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table"))
            {
                changed = true;
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: cleared S_ATTR_DEBUG on .dotnet_eh_table in '{ObjectFile}'.");
            }
            else
            {
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: no .dotnet_eh_table debug attribute to clear in '{ObjectFile}'.");
            }

            if (ApplyExportsList)
            {
                var keep = ExportsFile.Length == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : MachoExportsDemoter.ParseExportsList(File.ReadAllLines(ExportsFile));
                var demoted = MachoExportsDemoter.Demote(data, keep);
                if (demoted > 0)
                {
                    changed = true;
                    Log.LogMessage(MessageImportance.Low,
                        $"AotAnywhere: demoted {demoted} external symbols in '{ObjectFile}' to private-external " +
                        $"({keep.Count} kept exported per '{(ExportsFile.Length == 0 ? "/dev/null" : ExportsFile)}').");
                }
                else
                {
                    Log.LogMessage(MessageImportance.Low,
                        $"AotAnywhere: no external symbols to demote in '{ObjectFile}'.");
                }
            }

            if (changed)
                File.WriteAllBytes(ObjectFile, data);
            return true;
        }
        catch (Exception ex) when (ex is IOException or MachoFormatException)
        {
            Log.LogError($"AotAnywhere: failed to patch '{ObjectFile}': {ex.Message}");
            return false;
        }
    }
}
