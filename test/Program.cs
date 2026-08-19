using System;

#if SELFTEST
if (args is ["--selftest"])
    return SelfTest.Run();
#endif

// GC + exception smoke before the greeting: a mislinked binary can boot and
// print fine yet die on its first garbage collection or unwind - zig's macOS
// link used to fold away the __managedcode range brackets and drop the
// .dotnet_eh_table GC-info section, and nothing in a print-and-exit run ever
// noticed (the jaz SIGABRT, crash-analysis/FINDINGS-root-cause-zig-managedcode.md).
// Force both paths in every CI run of this binary; stay silent on success so
// the "Hello World" output contract holds.
var junk = new object[64];
for (var i = 0; i < 4096; i++)
{
    junk[i % junk.Length] = new byte[512];
    if (i % 1024 == 0)
    {
        try { throw new InvalidOperationException("gc-smoke"); }
        catch (InvalidOperationException) { }
    }
}
GC.Collect();
GC.WaitForPendingFinalizers();
if (junk[63] is null)
    return 1;

Console.WriteLine("Hello World");
return 0;

#if SELFTEST
// Exercises the native interop surfaces beyond Hello World: real ICU
// (non-invariant globalization), zlib via System.IO.Compression, and
// OpenSSL via RSA sign/verify. Compiled only for the selftest publishes
// (AotAnywhereSelfTest=true -> net10.0): referencing GZipStream pulls
// System.IO.Compression.Native into the link, and on net8 both link
// flows drop -lz (no bundled zlib), so the default matrix binaries must
// not reference it.
static class SelfTest
{
    public static int Run()
    {
        // Real ICU: the Turkish dotted capital only comes from ICU data;
        // invariant globalization maps 'i' to plain 'I' (or refuses to
        // construct the culture outright).
        try
        {
            var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            if ("i".ToUpper(tr) != "İ")
                return Fail("ICU: tr-TR uppercasing produced the invariant mapping");
        }
        catch (Exception e)
        {
            return Fail($"ICU: {e.Message}");
        }

        var payload = System.Text.Encoding.UTF8.GetBytes(
            string.Concat(System.Linq.Enumerable.Repeat("AotAnywhere selftest ", 500)));

        // zlib (bundled zlib-ng on net9+) via System.IO.Compression.Native.
        using var compressed = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(
            compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(payload);
        }
        compressed.Position = 0;
        using var decompressed = new System.IO.MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(
            compressed, System.IO.Compression.CompressionMode.Decompress))
        {
            gz.CopyTo(decompressed);
        }
        if (!decompressed.ToArray().AsSpan().SequenceEqual(payload))
            return Fail("zlib: GZip roundtrip mismatch");

        // OpenSSL via System.Security.Cryptography.Native.OpenSsl (dynamic
        // libssl/libcrypto on the target system).
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var signature = rsa.SignData(
            payload,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        if (!rsa.VerifyData(
            payload, signature,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1))
            return Fail("OpenSSL: RSA verify failed");

        // GC + funclet smoke: the selftest path returns before the default
        // path's smoke runs, so force a collection and an unwind here too
        // (see the comment above Hello World).
        try { throw new InvalidOperationException("gc-smoke"); }
        catch (InvalidOperationException) { }
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Console.WriteLine("selftest ok");
        return 0;
    }

    static int Fail(string message)
    {
        Console.WriteLine($"selftest FAILED: {message}");
        return 1;
    }
}
#endif
