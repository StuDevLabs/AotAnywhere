using System.Text;

namespace AotAnywhere.Tasks;

public struct RenameResult
{
    public int Renamed;
    /// Sections whose renamed name would not fit the 8-byte inline field.
    public int SkippedLong;
    /// Initialized sections that /MERGE wanted in .bss - renaming those would
    /// silently grow the uninitialized image, so they stay put.
    public int SkippedInitialized;
    /// `$`-grouped sections (`name$suffix`), which are never merged - see the
    /// note on RenameSections.
    public int SkippedGrouped;
}

/// Implements /MERGE by renaming COFF sections in the object image, since
/// `zig cc` cannot pass /MERGE through to lld.
public static class CoffSectionRenamer
{
    const uint IMAGE_SCN_CNT_UNINITIALIZED_DATA = 0x80;

    /// Renames sections named exactly `from` to `to` directly in `data`.
    /// `$`-grouped members (`from$suffix`) are deliberately left alone, and
    /// malformed or non-object inputs (import members, resources) are
    /// untouched. Mutates `data` in place.
    ///
    /// Why grouped sections stay put: link.exe lays a merged group out in
    /// `$`-suffix order, so ILC's `/MERGE:.managedcode=.text` still leaves the
    /// bootstrapper's `.managedcode$A`/`$Z` brackets around the managed code
    /// (`.managedcode$I`) once all three are called `.text$*`. lld does not
    /// reproduce that ordering inside an output section that already has other
    /// contributors: renaming them puts `.text$A`/`.text$Z` a few bytes apart
    /// near the start of `.text` and `.text$I` about a megabyte further on
    /// (measured on net10.0 win-arm64). The bootstrapper then registers a
    /// 16-byte managed-code range, `GetCodeManagerForAddress` finds no manager
    /// for any managed PC, and the first stack walk - the first GC or the
    /// first exception - fail-fasts. Left unmerged, `.managedcode` keeps its
    /// own output section, where lld does order `$A`/`$I`/`$Z` correctly (the
    /// net8.0 win-x64 shape, whose link.rsp carries no /MERGE at all). It
    /// costs nothing: the merged and unmerged images come out the same size.
    public static RenameResult RenameSections(byte[] data, string from, string to)
    {
        var result = new RenameResult();
        if (data.Length < 20) return result;

        var machine = Rd16(data, 0);
        if (machine == 0 || machine == 0xffff) return result; // anonymous/import object

        var sectionCount = Rd16(data, 2);
        var symtabOffset = Rd32(data, 8);
        var symbolCount = Rd32(data, 12);
        var optHeaderSize = Rd16(data, 16);
        long strtabOffset = symtabOffset != 0 ? symtabOffset + (long)symbolCount * 18 : 0;

        for (var i = 0; i < sectionCount; i++)
        {
            var header = 20 + (long)optHeaderSize + (long)i * 40;
            if (header + 40 > data.Length) return result;

            var name = SectionName(data, (int)header, strtabOffset);
            if (name == null) continue;

            if (name != from)
            {
                if (name.Length <= from.Length ||
                    !name.StartsWith(from, StringComparison.Ordinal) ||
                    name[from.Length] != '$')
                    continue;
                result.SkippedGrouped++;
                continue;
            }

            if (to.Length > 8) { result.SkippedLong++; continue; }

            var characteristics = Rd32(data, (int)header + 36);
            if (to == ".bss" && (characteristics & IMAGE_SCN_CNT_UNINITIALIZED_DATA) == 0)
            {
                result.SkippedInitialized++;
                continue;
            }

            for (var b = 0; b < 8; b++) data[header + b] = 0;
            for (var b = 0; b < to.Length; b++) data[header + b] = (byte)to[b];
            result.Renamed++;
        }

        return result;
    }

    /// Resolves a section header's name: inline (nul-padded, up to 8 bytes) or
    /// a "/nnn" decimal offset into the string table. Null when unparseable.
    public static string? SectionName(byte[] data, int header, long strtabOffset)
    {
        if (data[header] == (byte)'/')
        {
            var end = header + 1;
            while (end < header + 8 && data[end] != 0) end++;
            var digits = Encoding.ASCII.GetString(data, header + 1, end - (header + 1));
            if (!uint.TryParse(digits, out var offset)) return null;
            if (strtabOffset == 0) return null;
            var start = strtabOffset + offset;
            if (start >= data.Length) return null;
            var nul = -1;
            for (var k = (int)start; k < data.Length; k++)
                if (data[k] == 0) { nul = k; break; }
            if (nul < 0) return null;
            return Encoding.ASCII.GetString(data, (int)start, nul - (int)start);
        }

        var len = 0;
        while (len < 8 && data[header + len] != 0) len++;
        return Encoding.ASCII.GetString(data, header, len);
    }

    static ushort Rd16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));

    static uint Rd32(byte[] d, int o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
}
