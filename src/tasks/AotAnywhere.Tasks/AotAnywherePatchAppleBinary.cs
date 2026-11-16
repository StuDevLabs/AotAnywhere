using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace AotAnywhere.Tasks;

/// Repairs a zig-linked macOS image in place, immediately after the link:
/// on x86_64, undoes the +4 zig adds to every pcrel|sdata4 FDE LSDA pointer
/// (see MachoEhFrameLsdaFixer), without which the first managed exception
/// crashes the process. A no-op for arm64 images.
///
/// Must run exactly once per fresh link (the fix is not idempotent), so it
/// lives inside the link target, after the zig invocation, where incremental
/// builds skip both together. Runs before any code signing: zig leaves x86_64
/// images unsigned, and the opt-in rcodesign step signs the patched file.
public sealed class AotAnywherePatchAppleBinary : MSBuildTask
{
    /// The image the zig link just wrote ($(NativeBinary)).
    [Required] public string BinaryFile { get; set; } = "";

    public override bool Execute()
    {
        try
        {
            var data = File.ReadAllBytes(BinaryFile);
            var fixedCount = MachoEhFrameLsdaFixer.Fix(data);
            if (fixedCount > 0)
            {
                File.WriteAllBytes(BinaryFile, data);
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: corrected {fixedCount} __eh_frame LSDA pointers in '{BinaryFile}' (zig x86_64 +4).");
            }
            else
            {
                Log.LogMessage(MessageImportance.Low,
                    $"AotAnywhere: no __eh_frame LSDA pointers to correct in '{BinaryFile}'.");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or MachoFormatException)
        {
            Log.LogError($"AotAnywhere: failed to patch '{BinaryFile}': {ex.Message}");
            return false;
        }
    }
}
