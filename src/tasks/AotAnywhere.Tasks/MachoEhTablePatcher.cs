namespace AotAnywhere.Tasks;

public sealed class MachoFormatException : Exception
{
    public MachoFormatException(string message) : base(message) { }
}

/// Pure Mach-O surgery for the ILC relocatable object on Apple targets:
/// clears S_ATTR_DEBUG on the __DATA,.dotnet_eh_table section header.
///
/// ILC marks .dotnet_eh_table - the section holding every managed method's
/// unwind-block/GC-info blob, reached at run time through the compact-unwind
/// LSDA table - with S_ATTR_DEBUG. Apple's ld keeps the section anyway, but
/// zig's Mach-O linker kills every atom in a debug-attributed section, so the
/// whole table vanishes from the linked binary and each __unwind_info LSDA
/// entry silently degrades to a bogus pointer at the first __text atom. The
/// first GC root-scan then reads garbage GC info and crashes (SIGBUS in
/// UnixNativeCodeManager::GetCodeOffset). Clearing the attribute makes zig
/// treat the section as the ordinary __DATA payload it really is.
public static class MachoEhTablePatcher
{
    const uint MhMagic64 = 0xFEEDFACF;
    const uint MhObject = 0x1;
    const uint LcSegment64 = 0x19;
    const uint SAttrDebug = 0x02000000;

    /// Clears S_ATTR_DEBUG on `sectionName` in a 64-bit little-endian Mach-O
    /// relocatable object, in place in `data`. Returns true when the buffer
    /// was modified; false when the section is absent or already clear.
    /// Throws MachoFormatException when `data` is not the MH_OBJECT expected.
    public static bool ClearDebugAttr(byte[] data, string sectionName)
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
        var changed = false;

        long offset = 32;
        for (uint i = 0; i < ncmds; i++)
        {
            if (offset + 8 > data.Length)
                throw new MachoFormatException("truncated load commands");

            var cmd = Rd32(data, (int)offset);
            var cmdsize = Rd32(data, (int)offset + 4);
            if (cmdsize < 8 || offset + cmdsize > data.Length)
                throw new MachoFormatException("bad load command size");

            if (cmd == LcSegment64)
            {
                // segment_command_64: cmd cmdsize segname[16] vmaddr vmsize
                // fileoff filesize maxprot initprot nsects flags; section
                // headers (80 bytes each) follow at +72.
                if (cmdsize < 72)
                    throw new MachoFormatException("truncated segment command");

                var nsects = Rd32(data, (int)offset + 8 + 56);
                if (72 + (long)nsects * 80 > cmdsize)
                    throw new MachoFormatException("section headers past segment command");

                for (uint s = 0; s < nsects; s++)
                {
                    var so = (int)(offset + 72 + (long)s * 80);
                    if (!SectionNameEquals(data, so, sectionName))
                        continue;

                    // section_64 flags live at +64 (sectname[16] segname[16]
                    // addr(8) size(8) offset(4) align(4) reloff(4) nreloc(4)).
                    var flagsOffset = so + 64;
                    var flags = Rd32(data, flagsOffset);
                    if ((flags & SAttrDebug) != 0)
                    {
                        Wr32(data, flagsOffset, flags & ~SAttrDebug);
                        changed = true;
                    }
                }
            }

            offset += cmdsize;
        }

        return changed;
    }

    static bool SectionNameEquals(byte[] data, int nameOffset, string name)
    {
        if (name.Length > 16)
            return false;
        for (var i = 0; i < 16; i++)
        {
            var expected = i < name.Length ? (byte)name[i] : (byte)0;
            if (data[nameOffset + i] != expected)
                return false;
        }
        return true;
    }

    static uint Rd32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    static void Wr32(byte[] d, int o, uint v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 24);
    }
}
