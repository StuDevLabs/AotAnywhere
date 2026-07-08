namespace AotAnywhere.MSBuild.Tests;

// The file-scope activation flags that decide which link flow runs. These are
// pure RID/NativeLib logic (host-independent), evaluated without running any
// target.
public class ActivationTests
{
    static Dictionary<string, string> Rid(string rid, string nativeLib = "") =>
        new() { ["RuntimeIdentifier"] = rid, ["NativeLib"] = nativeLib };

    [Test]
    [Arguments("linux-x64", "", "true")]
    [Arguments("linux-musl-x64", "", "true")]
    [Arguments("linux-arm", "", "true")]
    [Arguments("linux-x64", "Static", "false")] // Static archives via CppLibCreator, no direct link
    [Arguments("osx-arm64", "", "false")]
    [Arguments("win-x64", "", "false")]
    public async Task LinuxDirectLinkActive(string rid, string nativeLib, string expected) =>
        await Assert.That(Harness.EvalProp(Rid(rid, nativeLib), "_AotAnywhereDirectLinkActive"))
            .IsEqualTo(expected);

    [Test]
    [Arguments("osx-arm64", "", "true")]
    [Arguments("osx-x64", "", "true")]
    [Arguments("osx-arm64", "Shared", "true")]
    [Arguments("osx-arm64", "Static", "false")]
    [Arguments("linux-x64", "", "false")]
    [Arguments("win-x64", "", "false")]
    public async Task MacDirectLinkActive(string rid, string nativeLib, string expected) =>
        await Assert.That(Harness.EvalProp(Rid(rid, nativeLib), "_AotAnywhereMacDirectLinkActive"))
            .IsEqualTo(expected);

    // Windows-cross is host-dependent: it triggers only when NOT on Windows.
    [Test]
    public async Task WindowsCrossReflectsHost()
    {
        var onWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        await Assert.That(Harness.EvalProp(Rid("win-x64"), "_AotAnywhereWindowsCross"))
            .IsEqualTo(onWindows ? "false" : "true");
        // Never active for a non-Windows target.
        await Assert.That(Harness.EvalProp(Rid("linux-x64"), "_AotAnywhereWindowsCross"))
            .IsEqualTo("false");
    }
}
