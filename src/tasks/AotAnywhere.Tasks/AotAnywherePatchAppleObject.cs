using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace AotAnywhere.Tasks;

/// Prepares the ILC relocatable object for a zig macOS link: clears
/// S_ATTR_DEBUG on __DATA,.dotnet_eh_table so zig's Mach-O linker does not
/// drop the managed GC-info table from the binary (which would make the
/// first garbage collection crash - see MachoEhTablePatcher).
///
/// Runs in place on $(NativeObject), immediately before the link. Idempotent:
/// the file is rewritten only when the attribute is actually set, so
/// incremental builds see a stable timestamp.
public sealed class AotAnywherePatchAppleObject : MSBuildTask
{
    /// The ILC-produced Mach-O relocatable object ($(NativeObject)).
    [Required] public string ObjectFile { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            var data = File.ReadAllBytes(ObjectFile);
            if (MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table"))
            {
                File.WriteAllBytes(ObjectFile, data);
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: cleared S_ATTR_DEBUG on .dotnet_eh_table in '{ObjectFile}'.");
            }
            else
            {
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: no .dotnet_eh_table debug attribute to clear in '{ObjectFile}'.");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or MachoFormatException)
        {
            Log.LogError($"AotAnywhere: failed to patch '{ObjectFile}': {ex.Message}");
            return false;
        }
    }
}
