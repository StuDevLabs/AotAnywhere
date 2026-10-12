using System.Text;

namespace AotAnywhere.Tasks;

/// Applies the SDK's exported-symbols list to the static libraries of an
/// Apple link: MachoExportsDemoter run over every Mach-O object member of a
/// BSD `ar` archive.
///
/// The standard pipeline's ld64 applies `-exported_symbols_list` to every
/// input, so the few hundred externals the runtime's static libraries define
/// (`GlobalizationNative_*`, `SystemNative_*`, ...) end up private too. zig's
/// linker ignores the list, and demoting only the ILC object (as
/// AotAnywherePatchAppleObject does) leaves those exported. Setting N_PEXT on
/// the members has the same effect as for the object: they still resolve
/// within the link, are emitted hidden, and -x drops them.
///
/// Only a bit changes, so every member keeps its size and offset and the
/// archive's symbol index (`__.SYMDEF`) stays valid - it lists the same names
/// in the same members, which is what member lookup needs. Members that are
/// not 64-bit little-endian MH_OBJECTs (the symbol index, anything else) are
/// left alone. Operates on a copy the caller provides; the NuGet cache copy
/// of a runtime library is never modified.
public static class MachoArchiveDemoter
{
    static readonly byte[] ArMagic = Encoding.ASCII.GetBytes("!<arch>\n");
    const int HeaderSize = 60;

    public static bool IsArchive(byte[] data)
    {
        if (data.Length < ArMagic.Length)
            return false;
        for (var i = 0; i < ArMagic.Length; i++)
            if (data[i] != ArMagic[i])
                return false;
        return true;
    }

    /// Demotes, in place, every defined external not in `keep` across the
    /// archive's Mach-O object members. Returns the total number of symbols
    /// demoted. Throws MachoFormatException when `data` is not a well-formed
    /// archive, or a Mach-O member is malformed.
    public static int Demote(byte[] data, ISet<string> keep)
    {
        if (!IsArchive(data))
            throw new MachoFormatException("not an ar archive");

        var demoted = 0;
        long offset = ArMagic.Length;
        while (offset < data.Length)
        {
            // A trailing newline pad may end the file.
            if (offset + 1 == data.Length && data[offset] == (byte)'\n')
                break;
            if (offset + HeaderSize > data.Length)
                throw new MachoFormatException("truncated archive member header");

            var header = (int)offset;
            if (data[header + 58] != (byte)'`' || data[header + 59] != (byte)'\n')
                throw new MachoFormatException("bad archive member header");

            var name = Ascii(data, header, 16).TrimEnd(' ');
            var size = ParseDecimal(Ascii(data, header + 48, 10), "member size");
            var dataStart = offset + HeaderSize;
            if (dataStart + size > data.Length)
                throw new MachoFormatException("archive member past end of file");

            // BSD long names: "#1/<len>", the name stored at the start of the
            // member data and counted in its size.
            long nameLen = 0;
            if (name.StartsWith("#1/", StringComparison.Ordinal))
            {
                nameLen = ParseDecimal(name.Substring(3), "extended name length");
                if (nameLen > size)
                    throw new MachoFormatException("archive member name longer than member");
            }

            var bodyStart = (int)(dataStart + nameLen);
            var bodyLen = (int)(size - nameLen);
            if (IsMachoObject(data, bodyStart, bodyLen))
            {
                var member = new byte[bodyLen];
                Buffer.BlockCopy(data, bodyStart, member, 0, bodyLen);
                var count = MachoExportsDemoter.Demote(member, keep);
                if (count > 0)
                {
                    Buffer.BlockCopy(member, 0, data, bodyStart, bodyLen);
                    demoted += count;
                }
            }

            offset = dataStart + size;
            if ((offset & 1) != 0)
                offset++;
        }

        return demoted;
    }

    static bool IsMachoObject(byte[] data, int start, int length) =>
        length >= 32 &&
        MachoObject.Rd32(data, start) == 0xFEEDFACF &&
        MachoObject.Rd32(data, start + 12) == 0x1;

    static string Ascii(byte[] data, int start, int length) => Encoding.ASCII.GetString(data, start, length);

    static long ParseDecimal(string field, string what)
    {
        var s = field.Trim();
        if (s.Length == 0 || !long.TryParse(s, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new MachoFormatException($"bad archive {what} '{field}'");
        return value;
    }
}
