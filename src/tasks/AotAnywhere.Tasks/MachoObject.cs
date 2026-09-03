namespace AotAnywhere.Tasks;

public sealed class MachoFormatException : Exception
{
    public MachoFormatException(string message) : base(message) { }
}

/// Shared plumbing for the in-place surgeries on the ILC relocatable object
/// (MachoEhTablePatcher, MachoExportsDemoter): header validation, the load
/// command walk, and little-endian field access.
internal static class MachoObject
{
    const uint MhMagic64 = 0xFEEDFACF;
    const uint MhObject = 0x1;

    public const uint LcSymtab = 0x2;
    public const uint LcSegment64 = 0x19;

    public delegate void LoadCommandVisitor(uint cmd, int offset, uint cmdsize);

    /// Validates that `data` is a 64-bit little-endian MH_OBJECT and calls
    /// `visit` for each load command with its type, file offset and size.
    /// Throws MachoFormatException on anything else.
    public static void ForEachLoadCommand(byte[] data, LoadCommandVisitor visit)
    {
        if (data.Length < 32)
            throw new MachoFormatException("truncated Mach-O header");

        var magic = Rd32(data, 0);
        if (magic != MhMagic64)
            throw new MachoFormatException($"not a 64-bit little-endian Mach-O (magic 0x{magic:x8})");

        var filetype = Rd32(data, 12);
        if (filetype != MhObject)
            throw new MachoFormatException($"not a relocatable object (filetype {filetype})");

        var ncmds = Rd32(data, 16);

        long offset = 32;
        for (uint i = 0; i < ncmds; i++)
        {
            if (offset + 8 > data.Length)
                throw new MachoFormatException("truncated load commands");

            var cmd = Rd32(data, (int)offset);
            var cmdsize = Rd32(data, (int)offset + 4);
            if (cmdsize < 8 || offset + cmdsize > data.Length)
                throw new MachoFormatException("bad load command size");

            visit(cmd, (int)offset, cmdsize);
            offset += cmdsize;
        }
    }

    public static uint Rd32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    public static void Wr32(byte[] d, int o, uint v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 24);
    }
}
