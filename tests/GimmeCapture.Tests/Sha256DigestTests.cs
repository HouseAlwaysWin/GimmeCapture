using System.Security.Cryptography;
using System.Text;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The instance key's SHA-256 (<see cref="Sha256Digest"/>) is the app's own, so that a start does not load .NET's
/// crypto library for it: it must give .NET's digest for every input, or each install would look for its settings in a
/// new folder.
/// </summary>
public class Sha256DigestTests
{
    [Theory]
    [InlineData("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData(
        "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq",
        "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public void KnownMessages_GiveTheStandardsDigests(string message, string digest)
    {
        Assert.Equal(digest, Convert.ToHexStringLower(Sha256Digest.HashData(Encoding.ASCII.GetBytes(message))));
    }

    [Fact]
    public void EveryLengthAcrossTheBlockBoundaries_GivesDotNetsDigest()
    {
        // Lengths 0-300 cross the padding's edge cases: 55, 56 and 64 bytes, and several blocks.
        var random = new Random(20261107);
        for (int length = 0; length <= 300; length++)
        {
            byte[] message = new byte[length];
            random.NextBytes(message);

            Assert.Equal(SHA256.HashData(message), Sha256Digest.HashData(message));
        }
    }

    [Fact]
    public void AnInstallPath_KeepsItsInstanceKey()
    {
        string path = @"C:\Program Files\GimmeCapture";
        string normalized = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))[..8]);

        Assert.Equal(expected, AppStoragePaths.GetInstallInstanceKey(path));
    }
}
