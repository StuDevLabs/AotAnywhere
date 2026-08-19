using AotAnywhere.Tasks;

namespace AotAnywhere.MSBuild.Tests;

// MachoEhTablePatcher clears S_ATTR_DEBUG on __DATA,.dotnet_eh_table in the
// ILC object before a zig macOS link: zig's linker kills atoms in
// debug-attributed sections, and losing the managed GC-info table makes the
// first garbage collection read garbage LSDA data and crash. These tests run
// the pure surgery against synthetic MH_OBJECT images.
public class MachoEhTablePatcherTests
{
    const uint SAttrDebug = 0x02000000;

    static byte[] BuildObject(params (string Name, uint Flags)[] sections)
    {
        var segCmdSize = 72 + sections.Length * 80;
        var data = new byte[32 + segCmdSize];
        Wr32(data, 0, 0xFEEDFACF);            // MH_MAGIC_64
        Wr32(data, 4, 0x0100000C);            // cputype arm64
        Wr32(data, 12, 0x1);                  // MH_OBJECT
        Wr32(data, 16, 1);                    // ncmds
        Wr32(data, 20, (uint)segCmdSize);     // sizeofcmds
        Wr32(data, 32, 0x19);                 // LC_SEGMENT_64
        Wr32(data, 36, (uint)segCmdSize);
        Wr32(data, 32 + 8 + 56, (uint)sections.Length); // nsects
        for (var i = 0; i < sections.Length; i++)
        {
            var so = 32 + 72 + i * 80;
            var name = System.Text.Encoding.ASCII.GetBytes(sections[i].Name);
            Array.Copy(name, 0, data, so, name.Length);          // sectname
            data[so + 16] = (byte)'_'; data[so + 17] = (byte)'_'; // segname "__DATA"-ish
            Wr32(data, so + 64, sections[i].Flags);              // flags
        }
        return data;
    }

    static uint SectionFlags(byte[] data, int index) =>
        (uint)(data[32 + 72 + index * 80 + 64] |
               (data[32 + 72 + index * 80 + 65] << 8) |
               (data[32 + 72 + index * 80 + 66] << 16) |
               (data[32 + 72 + index * 80 + 67] << 24));

    static void Wr32(byte[] d, int o, uint v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 24);
    }

    [Test]
    public async Task ClearsDebugAttrOnEhTable()
    {
        var data = BuildObject((".dotnet_eh_table", SAttrDebug));
        var changed = MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table");
        await Assert.That(changed).IsTrue();
        await Assert.That(SectionFlags(data, 0)).IsEqualTo(0u);
    }

    [Test]
    public async Task IdempotentWhenAlreadyClear()
    {
        var data = BuildObject((".dotnet_eh_table", 0u));
        var changed = MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table");
        await Assert.That(changed).IsFalse();
    }

    [Test]
    public async Task NoOpWhenSectionAbsent()
    {
        var data = BuildObject(("__data", 0u));
        var changed = MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table");
        await Assert.That(changed).IsFalse();
    }

    [Test]
    public async Task LeavesOtherDebugSectionsAlone()
    {
        var data = BuildObject(("__debug_info", SAttrDebug), (".dotnet_eh_table", SAttrDebug));
        var changed = MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table");
        await Assert.That(changed).IsTrue();
        await Assert.That(SectionFlags(data, 0)).IsEqualTo(SAttrDebug);
        await Assert.That(SectionFlags(data, 1)).IsEqualTo(0u);
    }

    [Test]
    public async Task PreservesNonDebugFlagBits()
    {
        // type S_ZEROFILL-ish low bits plus other attrs must survive the clear
        var data = BuildObject((".dotnet_eh_table", SAttrDebug | 0x00000400u | 0x1u));
        var changed = MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table");
        await Assert.That(changed).IsTrue();
        await Assert.That(SectionFlags(data, 0)).IsEqualTo(0x00000401u);
    }

    [Test]
    public async Task RejectsNonObjectFiles()
    {
        var data = BuildObject((".dotnet_eh_table", SAttrDebug));
        Wr32(data, 12, 0x2); // MH_EXECUTE
        await Assert.That(() => MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table"))
            .Throws<MachoFormatException>();
    }

    [Test]
    public async Task RejectsForeignFiles()
    {
        await Assert.That(() => MachoEhTablePatcher.ClearDebugAttr(new byte[64], ".dotnet_eh_table"))
            .Throws<MachoFormatException>();
    }

    [Test]
    public async Task RejectsSegmentCommandTruncatedBeforeNsects()
    {
        // LC_SEGMENT_64 whose cmdsize stops short of the 72-byte fixed header,
        // so nsects at +64 lies past the end of the buffer
        var data = new byte[32 + 16];
        Wr32(data, 0, 0xFEEDFACF);
        Wr32(data, 12, 0x1);
        Wr32(data, 16, 1);
        Wr32(data, 20, 16);
        Wr32(data, 32, 0x19);
        Wr32(data, 36, 16); // cmdsize < 72
        await Assert.That(() => MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table"))
            .Throws<MachoFormatException>();
    }

    [Test]
    public async Task RejectsNsectsOverrunningSegmentCommand()
    {
        var data = BuildObject((".dotnet_eh_table", SAttrDebug));
        Wr32(data, 32 + 8 + 56, 4); // nsects claims 4 headers, cmdsize holds 1
        await Assert.That(() => MachoEhTablePatcher.ClearDebugAttr(data, ".dotnet_eh_table"))
            .Throws<MachoFormatException>();
    }
}
