namespace AotAnywhere.MSBuild.Tests;

// The heart of the macOS DirectLink logic: _AotAnywhereComputeMacLinkArgs turns
// the SDK-computed @(LinkerArg) into the zig cc command line. Running it with a
// forced cross configuration (_AotAnywhereMacHostLink=false, injected
// _MacSysroot) makes the reconstruction deterministic and host-independent.
//
// NeverEmitsGnuSectionFlags is the specific regression guard for the startup
// SIGSEGV that shipped when the reconstruction copied net11's
// -Wl,--gc-sections / --discard-all onto the net8 macho link.
public class MacLinkArgsTests
{
    static RunResult Run(string linkerArgs, string sysroot = "/opt/applesdk",
        string? stripSymbols = null, string exportsFile = "", string outputType = "exe")
    {
        var props = new Dictionary<string, string>
        {
            ["_AotAnywhereMacHostLink"] = "false", // force cross mode - deterministic
            ["_MacSysroot"] = sysroot,
            ["NativeObject"] = "/obj/Hello.o",
            ["NativeBinary"] = "/bin/Hello",
            ["ExportsFile"] = exportsFile,
            ["OutputType"] = outputType,
            ["TestLinkerArgs"] = linkerArgs,
        };
        if (stripSymbols is not null)
            props["StripSymbols"] = stripSymbols;
        var result = Harness.Run("_AotAnywhereComputeMacLinkArgs", props);
        if (!result.Success)
            throw new Exception($"_AotAnywhereComputeMacLinkArgs failed: {result.ErrorText}");
        return result;
    }

    static string[] Compute(string linkerArgs, string sysroot = "/opt/applesdk",
        string? stripSymbols = null, string exportsFile = "", string outputType = "exe") =>
        Run(linkerArgs, sysroot, stripSymbols, exportsFile, outputType).Items("_MacLinkArg");

    [Test]
    public async Task NeverEmitsGnuSectionFlags()
    {
        var args = Compute("-framework;Security;--target=aarch64-macos");
        await Assert.That(args.Any(a => a.Contains("--gc-sections"))).IsFalse();
        await Assert.That(args.Any(a => a.Contains("--discard-all"))).IsFalse();
    }

    [Test]
    public async Task DropsLdClassic()
    {
        var args = Compute("-ld_classic;-framework;Security;--target=aarch64-macos");
        await Assert.That(args.Any(a => a == "-ld_classic")).IsFalse();
    }

    [Test]
    public async Task InjectsSysrootSearchPaths()
    {
        var args = Compute("--target=aarch64-macos");
        await Assert.That(args.Any(a => a == "-F/opt/applesdk/System/Library/Frameworks")).IsTrue();
        await Assert.That(args.Any(a => a == "-L/opt/applesdk/usr/lib")).IsTrue();
        await Assert.That(args.Any(a => a == "-L/opt/applesdk/usr/lib/swift")).IsTrue();
    }

    [Test]
    public async Task RewritesSwiftLibPathIntoSysroot()
    {
        var args = Compute("-L/usr/lib/swift;--target=aarch64-macos");
        await Assert.That(args.Any(a => a == "-L/usr/lib/swift")).IsFalse();
        await Assert.That(args.Any(a => a == "-L/opt/applesdk/usr/lib/swift")).IsTrue();
    }

    [Test]
    public async Task SwiftOverlaysOnlyWhenRuntimeLinked()
    {
        await Assert.That(Compute("--target=aarch64-macos").Any(a => a == "-lswiftCoreFoundation"))
            .IsFalse();
        await Assert.That(Compute("-lswiftCore;--target=aarch64-macos").Any(a => a == "-lswiftCoreFoundation"))
            .IsTrue();
    }

    // The strip -x equivalent: StripSymbols=true (the SDK default) folds
    // ld64's -x (drop local symbols) and -S (drop the stabs debug map) into
    // the link line; Apple's post-link strip cannot run on zig-linked
    // binaries, so this is the only strip path (issue #62 - the unstripped
    // ILC local symbols and stabs tripled the binary size).
    [Test]
    public async Task StripSymbolsAddsLocalSymbolStripFlags()
    {
        var args = Compute("--target=aarch64-macos", stripSymbols: "true");
        await Assert.That(args.Any(a => a == "-Wl,-x")).IsTrue();
        await Assert.That(args.Any(a => a == "-Wl,-S")).IsTrue();
    }

    [Test]
    public async Task NoStripFlagsWhenStripSymbolsOff()
    {
        foreach (var args in new[] { Compute("--target=aarch64-macos", stripSymbols: "false"),
                                     Compute("--target=aarch64-macos") })
        {
            await Assert.That(args.Any(a => a == "-Wl,-x")).IsFalse();
            await Assert.That(args.Any(a => a == "-Wl,-S")).IsFalse();
        }
    }

    [Test]
    public async Task GsPadSourceIsLinkedLast()
    {
        var args = Compute("--target=aarch64-macos");
        await Assert.That(args[^1].Contains("aotanywhere-gs-pad.c")).IsTrue();
    }

    // The managed-code range bookends: zig's linker folds __TEXT,__managedcode
    // into __text, emptying the section$start/end brackets the NativeAOT
    // bootstrapper registers as the managed-code range - and an empty range
    // fail-fasts the first GC's stack root-scan. The bookend .s files define
    // the bracket symbols themselves; their position on the link line is
    // load-bearing (start immediately before the ILC object, end immediately
    // after), so guard the exact ordering.
    [Test]
    public async Task ManagedCodeBookendsBracketTheObject()
    {
        var args = Compute("--target=aarch64-macos");
        var start = Array.FindIndex(args, a => a.Contains("aotanywhere-managedcode-start.o"));
        var obj = Array.FindIndex(args, a => a.Contains("Hello.o"));
        var end = Array.FindIndex(args, a => a.Contains("aotanywhere-managedcode-end.o"));
        await Assert.That(start).IsGreaterThan(-1);
        await Assert.That(obj).IsEqualTo(start + 1);
        await Assert.That(end).IsEqualTo(obj + 1);
    }

    // zig's Mach-O linker ignores -exported_symbols_list in every spelling
    // (the bare form the SDK uses draws an "argument unused" warning - issue
    // #98), so the exports list never rides the link line; it is applied to
    // the ILC object by AotAnywherePatchAppleObject instead, gated by
    // _MacApplyExportsList, which mirrors when the SDK would pass a list:
    // always for an executable (/dev/null without a file), only with a file
    // for a shared library.
    [Test]
    public async Task NeverPassesExportedSymbolsList()
    {
        foreach (var args in new[]
                 {
                     Compute("--target=aarch64-macos"),
                     Compute("--target=aarch64-macos", exportsFile: "/obj/Hello.exports"),
                     Compute("--target=aarch64-macos", exportsFile: "/obj/Hello.exports", outputType: "Library"),
                 })
        {
            await Assert.That(args.Any(a => a.Contains("exported_symbols_list"))).IsFalse();
        }
    }

    [Test]
    public async Task AppliesExportsListLikeTheSdkWould()
    {
        await Assert.That(Run("--target=aarch64-macos").Prop("_MacApplyExportsList")).IsEqualTo("true");
        await Assert.That(Run("--target=aarch64-macos", outputType: "Exe").Prop("_MacApplyExportsList")).IsEqualTo("true");
        await Assert.That(Run("--target=aarch64-macos", exportsFile: "/obj/Hello.exports").Prop("_MacApplyExportsList")).IsEqualTo("true");
        await Assert.That(Run("--target=aarch64-macos", exportsFile: "/obj/HelloLib.exports", outputType: "Library").Prop("_MacApplyExportsList")).IsEqualTo("true");
        await Assert.That(Run("--target=aarch64-macos", outputType: "Library").Prop("_MacApplyExportsList")).IsEqualTo("false");
    }

    [Test]
    public async Task CarriesObjectOutputAndTriple()
    {
        var args = Compute("--target=aarch64-macos");
        await Assert.That(args.Any(a => a.Contains("Hello.o"))).IsTrue();
        await Assert.That(args.Any(a => a.StartsWith("-o ") && a.Contains("Hello"))).IsTrue();
        await Assert.That(args.Any(a => a == "--target=aarch64-macos")).IsTrue();
    }
}
