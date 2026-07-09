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
    static string[] Compute(string linkerArgs, string sysroot = "/opt/applesdk")
    {
        var result = Harness.Run("_AotAnywhereComputeMacLinkArgs", new Dictionary<string, string>
        {
            ["_AotAnywhereMacHostLink"] = "false", // force cross mode - deterministic
            ["_MacSysroot"] = sysroot,
            ["NativeObject"] = "/obj/Hello.o",
            ["NativeBinary"] = "/bin/Hello",
            ["ExportsFile"] = "",
            ["OutputType"] = "exe",
            ["TestLinkerArgs"] = linkerArgs,
        });
        if (!result.Success)
            throw new Exception($"_AotAnywhereComputeMacLinkArgs failed: {result.ErrorText}");
        return result.Items("_MacLinkArg");
    }

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

    [Test]
    public async Task GsPadSourceIsLinkedLast()
    {
        var args = Compute("--target=aarch64-macos");
        await Assert.That(args[^1].Contains("aotanywhere-gs-pad.c")).IsTrue();
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
