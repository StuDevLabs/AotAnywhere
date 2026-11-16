using AotAnywhere.Tasks;

namespace AotAnywhere.MSBuild.Tests;

// MachoEhFrameLsdaFixer undoes the +4 zig's Mach-O linker adds to every
// pcrel|sdata4 FDE LSDA pointer on x86_64. ILC (.NET 9+) uses that encoding
// for every managed method, so without the fix the first managed throw in a
// zig-linked osx-x64 binary reads a garbage EH-info offset and dies with
// SIGBUS. These tests run the pure surgery on synthetic linked images whose
// CIE/FDE bytes mirror a real net10 osx-x64 link.
public class MachoEhFrameLsdaFixerTests
{
    const uint CpuX86_64 = 0x01000007;
    const uint CpuArm64 = 0x0100000C;

    // ILC's CIE, verbatim: version 1, "zLR", code align 1, data align -8,
    // RA r16, augmentation data 2 bytes: LSDA enc 0x1b, FDE enc 0x1b; then
    // the initial CFA program.
    static readonly byte[] IlcCieBody = Hex("017a4c5200017810021b1b0c07089001");

    // clang's CIE for a .cfi_lsda 16 function: "zPLR" with an indirect
    // personality pointer (0x9b), absptr LSDA (0x10) and absptr FDE (0x10).
    static readonly byte[] ClangCieBody = Hex("017a504c5200017810079b0400000010100c0708900100");

    sealed class EhFrame
    {
        readonly List<byte> _b = new();
        public int Length => _b.Count;

        public int Cie(byte[] body)
        {
            var at = _b.Count;
            Add32((uint)(4 + body.Length)); // length (id + body)
            Add32(0);                       // CIE id
            _b.AddRange(body);
            return at;
        }

        /// Returns the file-relative offset of the LSDA pointer field.
        public int Fde(int cie, int pointerSize, ulong lsda)
        {
            var at = _b.Count;
            var bodyLength = 4 + 2 * pointerSize + 1 + pointerSize; // id, pc_begin, pc_range, aug len, lsda
            Add32((uint)bodyLength);
            Add32((uint)(at + 4 - cie));                  // CIE pointer: back from this field
            AddN(0x11223344, pointerSize);                // pc_begin
            AddN(0x61, pointerSize);                      // pc_range
            _b.Add((byte)pointerSize);                    // augmentation data length
            var lsdaField = _b.Count;
            AddN(lsda, pointerSize);
            return lsdaField;
        }

        public byte[] Terminated()
        {
            Add32(0);
            return _b.ToArray();
        }

        void Add32(uint v) => AddN(v, 4);

        void AddN(ulong v, int n)
        {
            for (var i = 0; i < n; i++) _b.Add((byte)(v >> (8 * i)));
        }
    }

    /// A minimal MH_EXECUTE: one LC_SEGMENT_64 "__TEXT" holding one section
    /// (named `sectname`) whose contents are `section`, placed after the
    /// load commands. Returns the image and the section's file offset.
    static (byte[] Image, int SectionOffset) BuildImage(uint cputype, string sectname, byte[] section)
    {
        const int segCmdSize = 72 + 80;
        var sectionOffset = 32 + segCmdSize;
        var data = new byte[sectionOffset + section.Length];
        Wr32(data, 0, 0xFEEDFACF);       // MH_MAGIC_64
        Wr32(data, 4, cputype);
        Wr32(data, 12, 0x2);             // MH_EXECUTE
        Wr32(data, 16, 1);               // ncmds
        Wr32(data, 20, segCmdSize);      // sizeofcmds
        Wr32(data, 32, 0x19);            // LC_SEGMENT_64
        Wr32(data, 36, segCmdSize);
        Ascii(data, 32 + 8, "__TEXT");
        Wr32(data, 32 + 8 + 56, 1);      // nsects
        var so = 32 + 72;
        Ascii(data, so, sectname);
        Ascii(data, so + 16, "__TEXT");
        Wr32(data, so + 40, (uint)section.Length); // size (low half)
        Wr32(data, so + 48, (uint)sectionOffset);  // offset
        Array.Copy(section, 0, data, sectionOffset, section.Length);
        return (data, sectionOffset);
    }

    [Test]
    public async Task SubtractsFourFromIlcSdata4LsdaPointersOnly()
    {
        var eh = new EhFrame();
        var ilc = eh.Cie(IlcCieBody);
        var clang = eh.Cie(ClangCieBody);
        var withLsda = eh.Fde(ilc, 4, 0x00015623);         // as zig wrote it (+4)
        var nullLsda = eh.Fde(ilc, 4, 0);                  // no LSDA: stays null
        var absptr = eh.Fde(clang, 8, 0xFFFFFFFFFFFFFFA2); // clang's p64 path: no +4 to undo
        var (image, sect) = BuildImage(CpuX86_64, "__eh_frame", eh.Terminated());
        var before = (byte[])image.Clone();

        var fixedCount = MachoEhFrameLsdaFixer.Fix(image);

        await Assert.That(fixedCount).IsEqualTo(1);
        await Assert.That(Rd32(image, sect + withLsda)).IsEqualTo(0x0001561Fu);
        await Assert.That(Rd32(image, sect + nullLsda)).IsEqualTo(0u);
        await Assert.That(Rd64(image, sect + absptr)).IsEqualTo(0xFFFFFFFFFFFFFFA2ul);
        // Nothing but that one field changed.
        var diffs = Enumerable.Range(0, image.Length).Where(i => image[i] != before[i]).ToArray();
        await Assert.That(diffs.All(i => i >= sect + withLsda && i < sect + withLsda + 4)).IsTrue();
    }

    [Test]
    public async Task NegativeLsdaOffsetsAreCorrectedToo()
    {
        var eh = new EhFrame();
        var ilc = eh.Cie(IlcCieBody);
        var field = eh.Fde(ilc, 4, unchecked((uint)-0x100));
        var (image, sect) = BuildImage(CpuX86_64, "__eh_frame", eh.Terminated());

        await Assert.That(MachoEhFrameLsdaFixer.Fix(image)).IsEqualTo(1);
        await Assert.That(Rd32(image, sect + field)).IsEqualTo(unchecked((uint)-0x104));
    }

    [Test]
    public async Task LeavesArm64ImagesAlone()
    {
        // zig's LSDA addend is 0 on arm64, so the same bytes are already right.
        var eh = new EhFrame();
        var ilc = eh.Cie(IlcCieBody);
        eh.Fde(ilc, 4, 0x00015623);
        var (image, _) = BuildImage(CpuArm64, "__eh_frame", eh.Terminated());
        var before = (byte[])image.Clone();

        await Assert.That(MachoEhFrameLsdaFixer.Fix(image)).IsEqualTo(0);
        await Assert.That(image.SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task NoEhFrameIsANoOp()
    {
        var (image, _) = BuildImage(CpuX86_64, "__text", new byte[16]);
        var before = (byte[])image.Clone();

        await Assert.That(MachoEhFrameLsdaFixer.Fix(image)).IsEqualTo(0);
        await Assert.That(image.SequenceEqual(before)).IsTrue();
    }

    [Test]
    public async Task TruncatedRecordThrowsFormatError()
    {
        var eh = new EhFrame();
        var ilc = eh.Cie(IlcCieBody);
        eh.Fde(ilc, 4, 0x10);
        var bytes = eh.Terminated();
        Wr32(bytes, 0, 0x1000); // CIE claims to run past the section
        var (image, _) = BuildImage(CpuX86_64, "__eh_frame", bytes);

        await Assert.That(() => MachoEhFrameLsdaFixer.Fix(image)).Throws<MachoFormatException>();
    }

    static byte[] Hex(string s) => Convert.FromHexString(s);

    static void Ascii(byte[] d, int o, string s) =>
        System.Text.Encoding.ASCII.GetBytes(s).CopyTo(d, o);

    static uint Rd32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    static ulong Rd64(byte[] d, int o) => Rd32(d, o) | ((ulong)Rd32(d, o + 4) << 32);

    static void Wr32(byte[] d, int o, uint v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 24);
    }
}
