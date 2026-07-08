using Microsoft.Build.Framework;
using MSBuildTask = Microsoft.Build.Utilities.Task;

namespace AotAnywhere.Tasks;

/// Symbol-strips a Linux Native AOT binary, replacing the llvm-objcopy
/// personality of the shim (objcopy_shim.zig). Mirrors the three llvm-objcopy
/// invocations the ILC strip does, in order:
///
///   --only-keep-debug <bin> <bin>.dbg   (KeepDebugSymbols: sidecar = full copy)
///   --strip-debug --strip-unneeded <bin>
///   --add-gnu-debuglink=<bin>.dbg <bin> (KeepDebugSymbols)
///
/// Pure ELF surgery lives in ElfStripper; this task is the file orchestration.
public sealed class AotAnywhereStrip : MSBuildTask
{
    /// The linked binary to strip in place.
    [Required] public string Binary { get; set; } = "";

    /// The debug sidecar path (<bin><NativeSymbolExt>). When KeepDebugSymbols is
    /// true it receives a full copy of the unstripped binary and is linked back
    /// via .gnu_debuglink.
    public string SymbolFile { get; set; } = "";

    /// $(NativeDebugSymbols): keep a debug sidecar and a .gnu_debuglink.
    public bool KeepDebugSymbols { get; set; }

    public override bool Execute()
    {
        var keepDebug = KeepDebugSymbols && SymbolFile.Length > 0;
        try
        {
            var data = File.ReadAllBytes(Binary);

            // 1. --only-keep-debug: the sidecar is a full copy of the still
            //    unstripped binary (validated so garbage fails here).
            if (keepDebug)
            {
                ElfStripper.ValidateParses(data);
                File.WriteAllBytes(SymbolFile, data);
            }

            // 2. --strip-debug --strip-unneeded, in place. A section-less image
            //    is a no-op. Written in place (truncate, not unlink+recreate) so
            //    the executable bit set by the link survives.
            try
            {
                WriteInPlace(Binary, ElfStripper.Strip(data));
            }
            catch (ElfNoSectionsException)
            {
                Log.LogMessage(MessageImportance.Low, $"AotAnywhere strip: '{Binary}' has no section headers; nothing to strip.");
            }

            // 3. --add-gnu-debuglink=<sidecar>: CRC over the sidecar as written.
            if (keepDebug)
            {
                var crc = ElfStripper.Crc32(File.ReadAllBytes(SymbolFile));
                var linked = ElfStripper.AddDebugLink(File.ReadAllBytes(Binary), Path.GetFileName(SymbolFile), crc);
                WriteInPlace(Binary, linked);
            }

            return !Log.HasLoggedErrors;
        }
        catch (Exception e)
        {
            Log.LogError($"AotAnywhere: ELF strip of '{Binary}' failed: {e.Message}");
            return false;
        }
    }

    /// Overwrites an existing file's contents (FileMode.Create truncates the
    /// same inode), preserving its Unix mode - notably the executable bit.
    static void WriteInPlace(string path, byte[] data) => File.WriteAllBytes(path, data);
}
