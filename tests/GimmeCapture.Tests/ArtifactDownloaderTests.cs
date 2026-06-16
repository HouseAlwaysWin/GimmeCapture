using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public sealed class ArtifactDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_VerifiesAndAtomicallyReplacesDestination()
    {
        var bytes = "verified artifact"u8.ToArray();
        using var client = CreateClient(bytes);
        var downloader = new ArtifactDownloader(client);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "artifact.bin");
        await File.WriteAllTextAsync(destination, "old");

        var result = await downloader.DownloadAsync(Descriptor(bytes), directory);

        Assert.Equal(destination, result);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_ChecksumMismatch_DoesNotReplaceExistingFile()
    {
        var bytes = "bad artifact"u8.ToArray();
        using var client = CreateClient(bytes);
        var downloader = new ArtifactDownloader(client);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "artifact.bin");
        await File.WriteAllTextAsync(destination, "old");
        var descriptor = Descriptor(bytes) with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<ArtifactIntegrityException>(
            () => downloader.DownloadAsync(descriptor, directory));

        Assert.Equal("old", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_TruncatedResponse_DoesNotCreateDestination()
    {
        var bytes = "short"u8.ToArray();
        using var client = CreateClient(bytes, contentLength: bytes.Length + 10);
        var downloader = new ArtifactDownloader(client);
        var directory = CreateTempDirectory();

        await Assert.ThrowsAsync<ArtifactIntegrityException>(
            () => downloader.DownloadAsync(Descriptor(bytes) with { ExpectedSize = bytes.Length + 10 }, directory));

        Assert.False(File.Exists(Path.Combine(directory, "artifact.bin")));
        Assert.False(File.Exists(Path.Combine(directory, "artifact.bin.part")));
    }

    [Fact]
    public async Task DownloadAsync_Cancellation_RemovesPartAndPreservesDestination()
    {
        using var client = new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var downloader = new ArtifactDownloader(client);
        var directory = CreateTempDirectory();
        var destination = Path.Combine(directory, "artifact.bin");
        await File.WriteAllTextAsync(destination, "old");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(Descriptor("ignored"u8.ToArray()), directory, cancellationToken: cancellation.Token));

        Assert.Equal("old", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public void SafeZipExtractor_RejectsPathTraversal()
    {
        var directory = CreateTempDirectory();
        var zipPath = Path.Combine(directory, "bad.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
            writer.Write("bad");
        }

        Assert.Throws<InvalidDataException>(
            () => SafeZipExtractor.ExtractToDirectory(zipPath, Path.Combine(directory, "extract"), overwriteFiles: true));
        Assert.False(File.Exists(Path.Combine(directory, "escaped.txt")));
    }

    private static ArtifactDescriptor Descriptor(byte[] bytes)
    {
        return new ArtifactDescriptor(
            new Uri("https://example.test/artifact.bin"),
            "artifact.bin",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.Length);
    }

    private static HttpClient CreateClient(byte[] bytes, long? contentLength = null)
    {
        return new HttpClient(new DelegateHandler((_, _) =>
        {
            var content = new ByteArrayContent(bytes);
            if (contentLength.HasValue)
            {
                content.Headers.ContentLength = contentLength;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
