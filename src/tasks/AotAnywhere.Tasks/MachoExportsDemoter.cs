using System.Text;

namespace AotAnywhere.Tasks;

/// Pure Mach-O surgery for the ILC relocatable object on Apple targets:
/// applies the SDK's exported-symbols list by demoting every defined external
/// symbol that is not on it to private-external (N_PEXT).
///
/// The standard pipeline hands ld64 `-exported_symbols_list <file>` (or
/// `/dev/null` for an executable with no list), which is what keeps a
/// NativeLib=Shared dylib's export trie down to its UnmanagedCallersOnly
/// entry points and lets `strip -x` drop the thousands of method symbols
/// older ILCs emit as externals. zig's Mach-O linker accepts the option in
/// every spelling (bare, -Wl,) and honours none of them - the bare form only
/// earns an "argument unused" warning (issue #98) - and rejects
/// -(un)exported_symbol outright. What it does honour is the N_PEXT bit on an
/// input symbol: a private external resolves within the link like any global
/// but is emitted hidden, so it never reaches the export trie and ld64's -x
/// (which the link line already passes for StripSymbols) drops it from the
/// symbol table. Setting that bit on the ILC object before the link is
/// therefore equivalent to the exports list for everything ILC emits. (The
/// runtime's static libraries are not rewritten, so the few hundred externals
/// they contribute stay exported; ld64 would have demoted those too.)
///
/// The surgery is on the relocatable input, so no re-signing of the output is
/// involved, and only a bit is set - re-running is a no-op.
public static class MachoExportsDemoter
{
    const byte NStab = 0xe0;
    const byte NPext = 0x10;
    const byte NType = 0x0e;
    const byte NExt = 0x01;
    const byte NSect = 0x0e;

    /// Sets N_PEXT on every defined (N_SECT) external symbol in the 64-bit
    /// little-endian Mach-O relocatable object in `data`, in place, except
    /// the names in `keep`. Undefined symbols, locals, stabs and symbols that
    /// are already private-external are left alone. Returns the number of
    /// symbols demoted (0 when there was nothing to do, e.g. on a re-run).
    /// Throws MachoFormatException when `data` is not the MH_OBJECT expected.
    public static int Demote(byte[] data, ISet<string> keep)
    {
        var demoted = 0;

        MachoObject.ForEachLoadCommand(data, (cmd, offset, cmdsize) =>
        {
            if (cmd != MachoObject.LcSymtab)
                return;

            // symtab_command: cmd cmdsize symoff nsyms stroff strsize.
            if (cmdsize < 24)
                throw new MachoFormatException("truncated symtab command");

            var symoff = MachoObject.Rd32(data, offset + 8);
            var nsyms = MachoObject.Rd32(data, offset + 12);
            var stroff = MachoObject.Rd32(data, offset + 16);
            var strsize = MachoObject.Rd32(data, offset + 20);
            if (symoff + (long)nsyms * 16 > data.Length)
                throw new MachoFormatException("symbol table past end of file");
            if (stroff + (long)strsize > data.Length)
                throw new MachoFormatException("string table past end of file");

            for (uint i = 0; i < nsyms; i++)
            {
                // nlist_64: n_strx(4) n_type(1) n_sect(1) n_desc(2) n_value(8)
                var so = (int)(symoff + (long)i * 16);
                var nType = data[so + 4];
                if ((nType & NStab) != 0 || (nType & NExt) == 0 ||
                    (nType & NType) != NSect || (nType & NPext) != 0)
                    continue;

                var name = ReadName(data, stroff, strsize, MachoObject.Rd32(data, so));
                if (keep.Contains(name))
                    continue;

                data[so + 4] = (byte)(nType | NPext);
                demoted++;
            }
        });

        return demoted;
    }

    /// Parses an ld64 exported-symbols list: one symbol per line, blank lines
    /// and `#` comments ignored. ILC writes plain names (`_hello_add`), which
    /// is all that is supported - no ld64 wildcards.
    public static HashSet<string> ParseExportsList(IEnumerable<string> lines)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            keep.Add(line);
        }
        return keep;
    }

    static string ReadName(byte[] data, uint stroff, uint strsize, uint strx)
    {
        if (strx >= strsize)
            throw new MachoFormatException("symbol name past end of string table");
        var start = (int)(stroff + strx);
        var end = start;
        var limit = (int)(stroff + strsize);
        while (end < limit && data[end] != 0)
            end++;
        return Encoding.UTF8.GetString(data, start, end - start);
    }
}
