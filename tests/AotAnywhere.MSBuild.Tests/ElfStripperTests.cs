using System.Text;
using AotAnywhere.Tasks;

namespace AotAnywhere.MSBuild.Tests;

// The full ELF strip is validated end-to-end by CI running the stripped Linux
// binaries, and was proven byte-identical to the retired objcopy_shim.zig. This
// guards the one pure, host-independent piece: the .gnu_debuglink CRC.
public class ElfStripperTests
{
    [Test]
    public async Task Crc32MatchesTheStandardCheckValue()
    {
        // The canonical CRC-32/ISO-HDLC check value for "123456789".
        await Assert.That(ElfStripper.Crc32(Encoding.ASCII.GetBytes("123456789")))
            .IsEqualTo(0xCBF43926u);
    }

    [Test]
    public async Task Crc32OfEmptyInputIsZero()
    {
        await Assert.That(ElfStripper.Crc32(Array.Empty<byte>())).IsEqualTo(0u);
    }

    [Test]
    public async Task NonElfInputIsRejected()
    {
        await Assert.That(() => ElfStripper.Strip(new byte[] { 1, 2, 3, 4, 5 }))
            .Throws<ElfFormatException>();
    }
}
