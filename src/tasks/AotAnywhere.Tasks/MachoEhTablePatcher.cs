namespace AotAnywhere.Tasks;

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
    const uint SAttrDebug = 0x02000000;

    /// Clears S_ATTR_DEBUG on `sectionName` in a 64-bit little-endian Mach-O
    /// relocatable object, in place in `data`. Returns true when the buffer
    /// was modified; false when the section is absent or already clear.
    /// Throws MachoFormatException when `data` is not the MH_OBJECT expected.
    public static bool ClearDebugAttr(byte[] data, string sectionName)
    {
        var changed = false;

        MachoObject.ForEachLoadCommand(data, (cmd, offset, cmdsize) =>
        {
            if (cmd != MachoObject.LcSegment64)
                return;

            // segment_command_64: cmd cmdsize segname[16] vmaddr vmsize
            // fileoff filesize maxprot initprot nsects flags; section
            // headers (80 bytes each) follow at +72.
            if (cmdsize < 72)
                throw new MachoFormatException("truncated segment command");

            var nsects = MachoObject.Rd32(data, offset + 8 + 56);
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
                var flags = MachoObject.Rd32(data, flagsOffset);
                if ((flags & SAttrDebug) != 0)
                {
                    MachoObject.Wr32(data, flagsOffset, flags & ~SAttrDebug);
                    changed = true;
                }
            }
        });

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
}
