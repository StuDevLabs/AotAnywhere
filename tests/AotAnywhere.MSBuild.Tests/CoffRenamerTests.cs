using System.Text;
using AotAnywhere.Tasks;

namespace AotAnywhere.MSBuild.Tests;

// Ports the link_shim.zig /MERGE COFF-surgery tests to the managed
// CoffSectionRenamer - the binary object rewriting that /MERGE requires and
// that no MSBuild XML can do (hence a compiled task).
public class CoffRenamerTests
{
    const int StrtabOffset = 20 + 3 * 40; // 140: symbol table (empty) sits at the string table

    // Minimal COFF object: header, three section headers (.text inline,
    // .managedcode$I via the string table, hydrated inline and uninitialized),
    // no symbols, and a string table. Port of buildTestCoff.
    static byte[] BuildTestCoff(bool hydratedInitialized)
    {
        var strtab = new byte[] { 0, 0, 0, 0 }
            .Concat(Encoding.ASCII.GetBytes(".managedcode$I"))
            .Concat(new byte[] { 0 })
            .ToArray();

        var buf = new byte[StrtabOffset + strtab.Length];

        WriteU16(buf, 0, 0x8664); // machine: amd64
        WriteU16(buf, 2, 3);      // section count
        WriteU32(buf, 8, StrtabOffset);
        WriteU32(buf, 12, 0);     // symbol count

        Encoding.ASCII.GetBytes(".text").CopyTo(buf, 20);       // section 1
        WriteU32(buf, 20 + 36, 0x60000020);                     // initialized code
        Encoding.ASCII.GetBytes("/4").CopyTo(buf, 60);          // section 2: /4 -> ".managedcode$I"
        WriteU32(buf, 60 + 36, 0x60000020);
        Encoding.ASCII.GetBytes("hydrated").CopyTo(buf, 100);   // section 3: exactly 8 name bytes
        WriteU32(buf, 100 + 36, hydratedInitialized ? 0xc0000040 : 0xc0000080);

        strtab.CopyTo(buf, StrtabOffset);
        WriteU32(buf, StrtabOffset, (uint)strtab.Length);       // size fixup
        return buf;
    }

    static void WriteU16(byte[] d, int o, ushort v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
    static void WriteU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24);
    }

    static string? NameAt(byte[] coff, int header) => CoffSectionRenamer.SectionName(coff, header, StrtabOffset);

    [Test]
    public async Task RenamesGroupedAndExactLengthSectionsInPlace()
    {
        var coff = BuildTestCoff(hydratedInitialized: false);

        await Assert.That(CoffSectionRenamer.RenameSections(coff, ".managedcode", ".text").Renamed).IsEqualTo(1);
        await Assert.That(NameAt(coff, 60)).IsEqualTo(".text$I");

        await Assert.That(CoffSectionRenamer.RenameSections(coff, "hydrated", ".bss").Renamed).IsEqualTo(1);
        await Assert.That(NameAt(coff, 100)).IsEqualTo(".bss");

        // Untouched section keeps its name; a second pass finds nothing.
        await Assert.That(NameAt(coff, 20)).IsEqualTo(".text");
        await Assert.That(CoffSectionRenamer.RenameSections(coff, ".managedcode", ".text").Renamed).IsEqualTo(0);
        await Assert.That(CoffSectionRenamer.RenameSections(coff, "hydrated", ".bss").Renamed).IsEqualTo(0);
    }

    [Test]
    public async Task RefusesInitializedDataIntoBss()
    {
        var coff = BuildTestCoff(hydratedInitialized: true);
        var r = CoffSectionRenamer.RenameSections(coff, "hydrated", ".bss");
        await Assert.That(r.Renamed).IsEqualTo(0);
        await Assert.That(r.SkippedInitialized).IsEqualTo(1);
        await Assert.That(NameAt(coff, 100)).IsEqualTo("hydrated");
    }

    [Test]
    public async Task RefusesNamesThatDoNotFitInline()
    {
        var coff = BuildTestCoff(hydratedInitialized: false);
        var r = CoffSectionRenamer.RenameSections(coff, ".managedcode", ".mycode"); // ".mycode$I" is 9 bytes
        await Assert.That(r.Renamed).IsEqualTo(0);
        await Assert.That(r.SkippedLong).IsEqualTo(1);
    }

    [Test]
    public async Task LeavesNonObjectInputsUntouched()
    {
        var importMember = new byte[24];
        WriteU16(importMember, 0, 0);      // machine 0 ...
        WriteU16(importMember, 2, 0xffff); // ... sig2 0xffff: anonymous/import object
        await Assert.That(CoffSectionRenamer.RenameSections(importMember, ".managedcode", ".text").Renamed).IsEqualTo(0);

        var garbage = new byte[] { 1, 2, 3 };
        await Assert.That(CoffSectionRenamer.RenameSections(garbage, "a", "b").Renamed).IsEqualTo(0);
    }
}
