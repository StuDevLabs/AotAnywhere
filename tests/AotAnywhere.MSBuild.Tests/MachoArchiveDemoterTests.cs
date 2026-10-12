using System.Text;
using AotAnywhere.Tasks;
using static AotAnywhere.MSBuild.Tests.MachoExportsDemoterTests;

namespace AotAnywhere.MSBuild.Tests;

// MachoArchiveDemoter extends the exports-list demotion from the ILC object
// to the runtime's static libraries, as ld64 applies the list to every input.
// Synthetic BSD archives built from the same synthetic objects as
// MachoExportsDemoterTests.
public class MachoArchiveDemoterTests
{
    // A BSD ar archive. Names longer than 16 chars (or with spaces) use the
    // "#1/<len>" extended form, as Apple's libtool writes them.
    static byte[] BuildArchive(params (string Name, byte[] Data)[] members)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("!<arch>\n"));
        foreach (var (name, data) in members)
        {
            var longName = name.Length > 16 || name.Contains(' ');
            var nameBytes = Encoding.ASCII.GetBytes(name);
            var size = data.Length + (longName ? nameBytes.Length : 0);
            var header = (longName ? $"#1/{nameBytes.Length}" : name).PadRight(16)
                + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "644".PadRight(8)
                + size.ToString().PadRight(10) + "`\n";
            bytes.AddRange(Encoding.ASCII.GetBytes(header));
            if (longName)
                bytes.AddRange(nameBytes);
            bytes.AddRange(data);
            if ((bytes.Count & 1) != 0)
                bytes.Add((byte)'\n');
        }
        return bytes.ToArray();
    }

    static HashSet<string> Keep(params string[] names) => new(names, StringComparer.Ordinal);

    static int Find(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }

    // n_type of symbol `index` in the object embedded at `objectStart`.
    static byte TypeAt(byte[] archive, int objectStart, int index) => archive[objectStart + 32 + 24 + index * 16 + 4];

    [Test]
    public async Task DemotesMembersAndKeepsListedSymbols()
    {
        // DotNetRuntimeDebugHeader is defined in the runtime archive and is on
        // ILC's exports list - it must stay exported.
        var gc = BuildObject(new Sym("_DotNetRuntimeDebugHeader", NSect | NExt), new Sym("_RhpGcAlloc", NSect | NExt));
        var native = BuildObject(new Sym("_SystemNative_Read", NSect | NExt));
        var archive = BuildArchive(("__.SYMDEF SORTED", new byte[] { 1, 2, 3, 4 }), ("gc.o", gc), ("pal_io_long_name.c.o", native));

        var demoted = MachoArchiveDemoter.Demote(archive, Keep("_DotNetRuntimeDebugHeader"));

        await Assert.That(demoted).IsEqualTo(2);
        var gcAt = Find(archive, gc.AsSpan(0, 32).ToArray());
        await Assert.That(TypeAt(archive, gcAt, 0)).IsEqualTo((byte)(NSect | NExt));
        await Assert.That(TypeAt(archive, gcAt, 1)).IsEqualTo((byte)(NSect | NExt | NPext));
    }

    [Test]
    public async Task OnlySymbolBitsChange()
    {
        var obj = BuildObject(new Sym("_a", NSect | NExt), new Sym("_b", NSect | NExt));
        var archive = BuildArchive(("__.SYMDEF", new byte[] { 9, 9 }), ("odd.o", obj), ("long_member_name_here.o", obj));
        var before = (byte[])archive.Clone();

        await Assert.That(MachoArchiveDemoter.Demote(archive, Keep())).IsEqualTo(4);

        await Assert.That(archive.Length).IsEqualTo(before.Length);
        var diffs = Enumerable.Range(0, archive.Length).Where(i => archive[i] != before[i]).ToList();
        await Assert.That(diffs.Count).IsEqualTo(4);
        await Assert.That(diffs.All(i => archive[i] == (before[i] | NPext))).IsTrue();
    }

    [Test]
    public async Task IdempotentAndNonMachoMembersIgnored()
    {
        var archive = BuildArchive(("readme.txt", Encoding.ASCII.GetBytes("not an object")), ("x.o", BuildObject(new Sym("_x", NSect | NExt))));
        await Assert.That(MachoArchiveDemoter.Demote(archive, Keep())).IsEqualTo(1);
        await Assert.That(MachoArchiveDemoter.Demote(archive, Keep())).IsEqualTo(0);
    }

    [Test]
    public async Task RejectsMalformedArchives()
    {
        await Assert.That(MachoArchiveDemoter.IsArchive(Encoding.ASCII.GetBytes("!<arch>\n"))).IsTrue();
        await Assert.That(() => MachoArchiveDemoter.Demote(new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, Keep()))
            .Throws<MachoFormatException>();

        var truncated = BuildArchive(("x.o", BuildObject(new Sym("_x", NSect | NExt))));
        await Assert.That(() => MachoArchiveDemoter.Demote(truncated.AsSpan(0, truncated.Length - 10).ToArray(), Keep()))
            .Throws<MachoFormatException>();
    }
}
