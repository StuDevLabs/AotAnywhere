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
}

/// Implements /MERGE by renaming COFF sections in the object image, since
/// `zig cc` cannot pass /MERGE through to lld. Faithful port of
/// link_shim.zig's renameCoffSections/coffSectionName.
public static class CoffSectionRenamer
{
    const uint IMAGE_SCN_CNT_UNINITIALIZED_DATA = 0x80;

    /// Renames sections named `from` (or `from$suffix`) to `to` (keeping the
    /// suffix) directly in `data`. Malformed or non-object inputs (import
    /// members, resources) are left untouched. Mutates `data` in place.
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

            var suffix = "";
            if (name != from)
            {
                if (name.Length <= from.Length ||
                    !name.StartsWith(from, StringComparison.Ordinal) ||
                    name[from.Length] != '$')
                    continue;
                suffix = name.Substring(from.Length);
            }

            if (to.Length + suffix.Length > 8) { result.SkippedLong++; continue; }

            var characteristics = Rd32(data, (int)header + 36);
            if (to == ".bss" && (characteristics & IMAGE_SCN_CNT_UNINITIALIZED_DATA) == 0)
            {
                result.SkippedInitialized++;
                continue;
            }

            var newName = to + suffix;
            for (var b = 0; b < 8; b++) data[header + b] = 0;
            for (var b = 0; b < newName.Length; b++) data[header + b] = (byte)newName[b];
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
