using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using ReactiveUI;
using ReactiveUI.Reactive;

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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
