using AotAnywhere.Tasks;

namespace AotAnywhere.MSBuild.Tests;

// MachoExportsDemoter applies the SDK's exported-symbols list to the ILC
// object before a zig macOS link: zig ignores -exported_symbols_list (issue
// #98), but honours N_PEXT on an input symbol, so every defined external not
// on the list gets that bit set. These tests run the pure surgery against
// synthetic MH_OBJECT images with a hand-built LC_SYMTAB.
public class MachoExportsDemoterTests
{
    const byte NExt = 0x01;
    const byte NPext = 0x10;
    const byte NSect = 0x0e;
    const byte NUndf = 0x00;
    const byte NAbs = 0x02;
    const byte NFun = 0x24;   // a stab (N_STAB bits set)

    record Sym(string Name, byte Type);

    // MH_OBJECT with a single LC_SYMTAB; symbols and the string table follow
    // the load commands.
    static byte[] BuildObject(params Sym[] syms)
    {
        var strtab = new List<byte> { 0 }; // index 0 is the empty name
        var strx = new uint[syms.Length];
        for (var i = 0; i < syms.Length; i++)
        {
            strx[i] = (uint)strtab.Count;
            strtab.AddRange(System.Text.Encoding.ASCII.GetBytes(syms[i].Name));
            strtab.Add(0);
        }

        var symoff = 32 + 24;
        var stroff = symoff + syms.Length * 16;
        var data = new byte[stroff + strtab.Count];
        Wr32(data, 0, 0xFEEDFACF);        // MH_MAGIC_64
        Wr32(data, 4, 0x0100000C);        // cputype arm64
        Wr32(data, 12, 0x1);              // MH_OBJECT
        Wr32(data, 16, 1);                // ncmds
        Wr32(data, 20, 24);               // sizeofcmds
        Wr32(data, 32, 0x2);              // LC_SYMTAB
        Wr32(data, 36, 24);
        Wr32(data, 40, (uint)symoff);
        Wr32(data, 44, (uint)syms.Length);
        Wr32(data, 48, (uint)stroff);
        Wr32(data, 52, (uint)strtab.Count);
        for (var i = 0; i < syms.Length; i++)
        {
            var so = symoff + i * 16;
            Wr32(data, so, strx[i]);
            data[so + 4] = syms[i].Type;
            data[so + 5] = 1;             // n_sect
        }
        strtab.CopyTo(data, stroff);
        return data;
    }

    static byte TypeOf(byte[] data, int index) => data[32 + 24 + index * 16 + 4];

    static void Wr32(byte[] d, int o, uint v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
        d[o + 2] = (byte)(v >> 16);
        d[o + 3] = (byte)(v >> 24);
    }

    static HashSet<string> Keep(params string[] names) => new(names, StringComparer.Ordinal);

    [Test]
    public async Task DemotesExternalsNotOnTheList()
    {
        var data = BuildObject(new Sym("_hello_add", NSect | NExt), new Sym("_S_P_CoreLib_Foo", NSect | NExt));
        var demoted = MachoExportsDemoter.Demote(data, Keep("_hello_add"));
        await Assert.That(demoted).IsEqualTo(1);
        await Assert.That(TypeOf(data, 0)).IsEqualTo((byte)(NSect | NExt));
        await Assert.That(TypeOf(data, 1)).IsEqualTo((byte)(NSect | NExt | NPext));
    }

    [Test]
    public async Task EmptyListDemotesEverything()
    {
        // the executable case: -exported_symbols_list /dev/null
        var data = BuildObject(new Sym("_a", NSect | NExt), new Sym("_b", NSect | NExt));
        var demoted = MachoExportsDemoter.Demote(data, Keep());
        await Assert.That(demoted).IsEqualTo(2);
    }

    [Test]
    public async Task LeavesUndefinedLocalsStabsAndAbsoluteAlone()
    {
        var data = BuildObject(
            new Sym("_undefined", NUndf | NExt),
            new Sym("ltmp0", NSect),
            new Sym("_stab", NFun),
            new Sym("_abs", NAbs | NExt));
        var demoted = MachoExportsDemoter.Demote(data, Keep());
        await Assert.That(demoted).IsEqualTo(0);
        await Assert.That(TypeOf(data, 0)).IsEqualTo((byte)(NUndf | NExt));
        await Assert.That(TypeOf(data, 1)).IsEqualTo(NSect);
        await Assert.That(TypeOf(data, 2)).IsEqualTo(NFun);
        await Assert.That(TypeOf(data, 3)).IsEqualTo((byte)(NAbs | NExt));
    }

    [Test]
    public async Task IdempotentOnRerun()
    {
        var data = BuildObject(new Sym("_a", NSect | NExt), new Sym("_keep", NSect | NExt));
        await Assert.That(MachoExportsDemoter.Demote(data, Keep("_keep"))).IsEqualTo(1);
        var snapshot = (byte[])data.Clone();
        await Assert.That(MachoExportsDemoter.Demote(data, Keep("_keep"))).IsEqualTo(0);
        await Assert.That(data).IsEquivalentTo(snapshot);
    }

    [Test]
    public async Task AlreadyPrivateExternalIsNotCounted()
    {
        var data = BuildObject(new Sym("_hidden", NSect | NExt | NPext));
        await Assert.That(MachoExportsDemoter.Demote(data, Keep())).IsEqualTo(0);
    }

    [Test]
    public async Task KeepMatchIsExactAndCaseSensitive()
    {
        var data = BuildObject(new Sym("_Hello_add", NSect | NExt), new Sym("_hello_add2", NSect | NExt));
        await Assert.That(MachoExportsDemoter.Demote(data, Keep("_hello_add"))).IsEqualTo(2);
    }

    [Test]
    public async Task ParsesIlcExportsFile()
    {
        var keep = MachoExportsDemoter.ParseExportsList(new[]
        {
            "_DotNetRuntimeDebugHeader",
            "_hello_add",
            "",
            "# a comment",
            "  _padded  ",
        });
        await Assert.That(keep).IsEquivalentTo(new[] { "_DotNetRuntimeDebugHeader", "_hello_add", "_padded" });
    }

    [Test]
    public async Task RejectsNonObjectFiles()
    {
        var data = BuildObject(new Sym("_a", NSect | NExt));
        Wr32(data, 12, 0x2); // MH_EXECUTE
        await Assert.That(() => MachoExportsDemoter.Demote(data, Keep()))
            .Throws<MachoFormatException>();
    }

    [Test]
    public async Task RejectsSymbolTablePastEndOfFile()
    {
        var data = BuildObject(new Sym("_a", NSect | NExt));
        Wr32(data, 44, 1000); // nsyms overruns the buffer
        await Assert.That(() => MachoExportsDemoter.Demote(data, Keep()))
            .Throws<MachoFormatException>();
    }

    [Test]
    public async Task RejectsNamePastEndOfStringTable()
    {
        var data = BuildObject(new Sym("_a", NSect | NExt));
        Wr32(data, 32 + 24, 4000); // n_strx beyond strsize
        await Assert.That(() => MachoExportsDemoter.Demote(data, Keep()))
            .Throws<MachoFormatException>();
    }

    [Test]
    public async Task NoSymtabIsANoOp()
    {
        var data = new byte[32];
        Wr32(data, 0, 0xFEEDFACF);
        Wr32(data, 12, 0x1);
        await Assert.That(MachoExportsDemoter.Demote(data, Keep())).IsEqualTo(0);
    }
}
