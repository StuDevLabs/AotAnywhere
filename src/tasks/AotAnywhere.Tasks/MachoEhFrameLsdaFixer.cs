namespace AotAnywhere.Tasks;

/// Pure Mach-O surgery on a zig-linked x86_64 image: undoes the +4 that zig's
/// Mach-O linker (0.17, still on master) adds to every 32-bit FDE LSDA pointer
/// it writes for x86_64 (src/link/MachO/eh_frame.zig: `addend = 4` for
/// x86_64, applied only on the p32 LSDA path).
///
/// .NET 9+'s ILC object writer gives its CIEs a pcrel|sdata4 LSDA encoding,
/// and on x86_64 every managed method carries a DWARF FDE whose LSDA the
/// unwinder hands to the runtime. With the +4 the pointer lands 4 bytes into
/// the method's unwind block (or 1 byte into the next one), exception dispatch
/// decodes garbage as the relative offset to the EH info, and the first
/// managed throw dies with SIGBUS in UnixNativeCodeManager::EHEnumInit.
/// clang emits absptr LSDA pointers, which take zig's p64 path (no +4), so
/// only pcrel|sdata4 CIEs are touched; arm64 images are left alone (zig's
/// addend is 0 there).
///
/// NOT idempotent: run it exactly once, on the image a zig link just wrote.
public static class MachoEhFrameLsdaFixer
{
    const byte DwEhPeOmit = 0xff;
    const byte DwEhPePcrelSdata4 = 0x1b;

    /// Corrects the FDE LSDA pointers in `data` in place. Returns how many
    /// were rewritten; 0 for non-x86_64 images or images without __eh_frame.
    /// Throws MachoFormatException on a malformed image or __eh_frame.
    public static int Fix(byte[] data)
    {
        if (data.Length >= 8 && MachoObject.Rd32(data, 0) == 0xFEEDFACF
            && MachoObject.Rd32(data, 4) != MachoObject.CpuTypeX86_64)
            return 0;

        var ehOffset = -1L;
        var ehSize = 0L;
        MachoObject.ForEachLoadCommand(data, (cmd, offset, cmdsize) =>
        {
            if (cmd != MachoObject.LcSegment64)
                return;
            var nsects = MachoObject.Rd32(data, offset + 8 + 56);
            if (72 + (long)nsects * 80 > cmdsize)
                throw new MachoFormatException("section headers past segment command");
            for (uint s = 0; s < nsects; s++)
            {
                var so = (int)(offset + 72 + (long)s * 80);
                if (!NameEquals(data, so, "__eh_frame") || !NameEquals(data, so + 16, "__TEXT"))
                    continue;
                ehSize = (long)Rd64(data, so + 40);
                ehOffset = MachoObject.Rd32(data, so + 48);
            }
        }, requireObject: false);

        if (ehOffset < 0)
            return 0;
        if (ehOffset + ehSize > data.Length)
            throw new MachoFormatException("__eh_frame past end of file");

        return FixEhFrame(data, (int)ehOffset, (int)(ehOffset + ehSize));
    }

    static int FixEhFrame(byte[] d, int start, int end)
    {
        // CIE offset -> (LSDA encoding, FDE pointer encoding, has 'z'), or
        // null when the augmentation could not be parsed (its FDEs are skipped).
        var cies = new Dictionary<int, (byte Lsda, byte Fde, bool Z)?>();
        var fixedCount = 0;
        var o = start;
        while (o + 4 <= end)
        {
            var length = MachoObject.Rd32(d, o);
            if (length == 0)
                break; // terminator
            if (length == 0xffffffff)
                throw new MachoFormatException("64-bit __eh_frame records are not supported");
            var idOffset = o + 4;
            var next = idOffset + (long)length;
            if (next > end)
                throw new MachoFormatException("__eh_frame record past end of section");

            var id = MachoObject.Rd32(d, idOffset);
            if (id == 0)
            {
                cies[o] = ParseCie(d, idOffset + 4, (int)next);
            }
            else
            {
                var cieOffset = idOffset - (long)id;
                if (cies.TryGetValue((int)cieOffset, out var cie) && cie is { } c && c.Z && c.Lsda == DwEhPePcrelSdata4)
                {
                    var p = idOffset + 4;
                    p += 2 * PointerSize(c.Fde); // pc_begin, pc_range
                    ReadUleb(d, ref p, (int)next); // augmentation data length
                    if (p + 4 > next)
                        throw new MachoFormatException("truncated FDE LSDA pointer");
                    var value = (int)MachoObject.Rd32(d, p);
                    if (value != 0)
                    {
                        MachoObject.Wr32(d, p, unchecked((uint)(value - 4)));
                        fixedCount++;
                    }
                }
            }
            o = (int)next;
        }
        return fixedCount;
    }

    static (byte Lsda, byte Fde, bool Z)? ParseCie(byte[] d, int p, int end)
    {
        var version = d[p++];
        var augEnd = Array.IndexOf(d, (byte)0, p, end - p);
        if (augEnd < 0)
            throw new MachoFormatException("unterminated CIE augmentation string");
        var aug = System.Text.Encoding.ASCII.GetString(d, p, augEnd - p);
        p = augEnd + 1;
        ReadUleb(d, ref p, end); // code alignment
        ReadUleb(d, ref p, end); // data alignment (sleb; only skipped)
        if (version == 1) p++; else ReadUleb(d, ref p, end); // return register

        if (!aug.StartsWith("z", StringComparison.Ordinal))
            return (DwEhPeOmit, 0, false);
        ReadUleb(d, ref p, end); // augmentation data length
        byte lsda = DwEhPeOmit, fde = 0;
        foreach (var ch in aug.Substring(1))
        {
            switch (ch)
            {
                case 'L': lsda = d[p++]; break;
                case 'R': fde = d[p++]; break;
                case 'P':
                    var enc = d[p++];
                    var size = PointerSize(enc);
                    if (size == 0) return null;
                    p += size;
                    break;
                case 'S': break;
                default: return null; // unknown augmentation: leave this CIE's FDEs alone
            }
        }
        // FDE pc_begin/pc_range must be fixed-size to find the LSDA pointer.
        return PointerSize(fde) == 0 ? null : (lsda, fde, true);
    }

    /// Size of a fixed-size DW_EH_PE-encoded pointer (low nibble), 0 if variable.
    static int PointerSize(byte enc) => (enc & 0x0f) switch
    {
        0x0 => 8,          // absptr (64-bit image)
        0x2 or 0xa => 2,   // udata2 / sdata2
        0x3 or 0xb => 4,   // udata4 / sdata4
        0x4 or 0xc => 8,   // udata8 / sdata8
        _ => 0,
    };

    static void ReadUleb(byte[] d, ref int p, int end)
    {
        while (true)
        {
            if (p >= end)
                throw new MachoFormatException("truncated LEB128 in __eh_frame");
            if ((d[p++] & 0x80) == 0)
                return;
        }
    }

    static ulong Rd64(byte[] d, int o) => MachoObject.Rd32(d, o) | ((ulong)MachoObject.Rd32(d, o + 4) << 32);

    static bool NameEquals(byte[] data, int offset, string name)
    {
        for (var i = 0; i < 16; i++)
        {
            var expected = i < name.Length ? (byte)name[i] : (byte)0;
            if (data[offset + i] != expected)
                return false;
        }
        return true;
    }
}
