using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using ReactiveUI;

namespace GimmeCapture.Services.Core.AI;

public class AIModelDownloader : ReactiveObject
{
    private readonly IArtifactDownloader _artifactDownloader;

    public AIModelDownloader()
        : this(new ArtifactDownloader(SharedHttpClient.Instance))
    {
    }

    public AIModelDownloader(IArtifactDownloader artifactDownloader)
    {
        _artifactDownloader = artifactDownloader ?? throw new ArgumentNullException(nameof(artifactDownloader));
    }

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set => this.RaiseAndSetIfChanged(ref _isDownloading, value);
    }

    private string _currentDownloadName = "AI Component";
    public string CurrentDownloadName
    {
        get => _currentDownloadName;
        set => this.RaiseAndSetIfChanged(ref _currentDownloadName, value);
    }

    private ArtifactDownloadStage _downloadStage;
    public ArtifactDownloadStage DownloadStage
    {
        get => _downloadStage;
        private set => this.RaiseAndSetIfChanged(ref _downloadStage, value);
    }

    public virtual async Task DownloadFileAsync(
        ArtifactDescriptor descriptor,
        string destination,
        double progressOffset,
        double progressWeight,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destination));
        var destinationDescriptor = descriptor with { FileName = Path.GetFileName(destination) };
        var progress = new InlineProgress<ArtifactDownloadProgress>(value =>
        {
            DownloadStage = value.Stage;
            DownloadProgress = progressOffset + (value.Percentage / 100 * progressWeight);
        });

        await _artifactDownloader
            .DownloadAsync(destinationDescriptor, destinationDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public virtual async Task DownloadAndExtractZipAsync(
        ArtifactDescriptor descriptor,
        string outputDirectory,
        double progressOffset,
        double progressWeight,
        CancellationToken cancellationToken)
    {
        var zipPath = Path.Combine(outputDirectory, descriptor.FileName);
        await DownloadFileAsync(descriptor, zipPath, progressOffset, progressWeight, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    entry.ExtractToFile(Path.Combine(outputDirectory, fileName), overwrite: true);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
        }
    }

    [Obsolete("Use the ArtifactDescriptor overload so downloads are integrity checked.")]
    public virtual Task DownloadFileAsync(
        string url,
        string destination,
        double progressOffset,
        double progressWeight,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unverified URL downloads are disabled.");

    [Obsolete("Use the ArtifactDescriptor overload so downloads are integrity checked.")]
    public virtual Task DownloadAndExtractZipAsync(
        string url,
        string outputDirectory,
        double progressOffset,
        double progressWeight,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unverified URL downloads are disabled.");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
