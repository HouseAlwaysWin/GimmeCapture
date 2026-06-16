using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed record ArtifactDescriptor(
    Uri Uri,
    string FileName,
    string Sha256,
    long ExpectedSize);

public enum ArtifactDownloadStage
{
    Downloading,
    Verifying,
    Completed,
    Cancelled,
    Failed
}

public sealed record ArtifactDownloadProgress(
    ArtifactDownloadStage Stage,
    double Percentage,
    long BytesReceived,
    long ExpectedSize);

public sealed class ArtifactIntegrityException(string message) : IOException(message);

public static class SharedHttpClient
{
    public static HttpClient Instance { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GimmeCapture/1.0");
        return client;
    }
}

public sealed class ArtifactDownloader(HttpClient httpClient) : IArtifactDownloader
{
    private const int BufferSize = 128 * 1024;

    public async Task<string> DownloadAsync(
        ArtifactDescriptor descriptor,
        string destinationDirectory,
        IProgress<ArtifactDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDescriptor(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, descriptor.FileName);
        var partPath = destinationPath + ".part";
        TryDelete(partPath);

        try
        {
            using var response = await httpClient.GetAsync(
                descriptor.Uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength &&
                descriptor.ExpectedSize > 0 &&
                contentLength != descriptor.ExpectedSize)
            {
                throw new ArtifactIntegrityException(
                    $"Content length mismatch for {descriptor.FileName}: expected {descriptor.ExpectedSize}, received {contentLength}.");
            }

            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                partPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long totalRead = 0;

            try
            {
                while (true)
                {
                    var read = await source
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await destination
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    totalRead += read;
                    progress?.Report(new ArtifactDownloadProgress(
                        ArtifactDownloadStage.Downloading,
                        CalculatePercentage(totalRead, descriptor.ExpectedSize),
                        totalRead,
                        descriptor.ExpectedSize));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            await destination.DisposeAsync().ConfigureAwait(false);

            progress?.Report(new ArtifactDownloadProgress(
                ArtifactDownloadStage.Verifying,
                100,
                totalRead,
                descriptor.ExpectedSize));

            if (descriptor.ExpectedSize > 0 && totalRead != descriptor.ExpectedSize)
            {
                throw new ArtifactIntegrityException(
                    $"Downloaded size mismatch for {descriptor.FileName}: expected {descriptor.ExpectedSize}, received {totalRead}.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArtifactIntegrityException(
                    $"SHA-256 mismatch for {descriptor.FileName}: expected {descriptor.Sha256}, received {actualHash}.");
            }

            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(new ArtifactDownloadProgress(
                ArtifactDownloadStage.Completed,
                100,
                totalRead,
                descriptor.ExpectedSize));
            return destinationPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            progress?.Report(new ArtifactDownloadProgress(
                ArtifactDownloadStage.Cancelled,
                0,
                0,
                descriptor.ExpectedSize));
            throw;
        }
        catch
        {
            TryDelete(partPath);
            progress?.Report(new ArtifactDownloadProgress(
                ArtifactDownloadStage.Failed,
                0,
                0,
                descriptor.ExpectedSize));
            throw;
        }
    }

    private static void ValidateDescriptor(ArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.Uri.IsAbsoluteUri)
        {
            throw new ArgumentException("Artifact URI must be absolute.", nameof(descriptor));
        }
        if (string.IsNullOrWhiteSpace(descriptor.FileName) ||
            !string.Equals(Path.GetFileName(descriptor.FileName), descriptor.FileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Artifact file name must not contain a path.", nameof(descriptor));
        }
        if (descriptor.Sha256.Length != 64 || !IsSha256(descriptor.Sha256))
        {
            throw new ArgumentException("Artifact SHA-256 must be a 64-character hexadecimal value.", nameof(descriptor));
        }
        if (descriptor.ExpectedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor));
        }
    }

    private static double CalculatePercentage(long received, long expected)
    {
        return expected > 0 ? Math.Clamp((double)received / expected * 100, 0, 100) : 0;
    }

    private static bool IsSha256(string value)
    {
        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
