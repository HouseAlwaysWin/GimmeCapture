using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.Services.Core.AI;

public class AIModelDownloader : ReactiveObject
{
    private readonly Lazy<IArtifactDownloader> _artifactDownloader;

    /// <summary>
    /// Downloads through the shared HTTP client, which is built when the first download starts. Built with this, as
    /// the main window's dependencies are made at start-up, it loaded the HTTP stack (System.Net.Http, Net.Security,
    /// Net.Primitives and DiagnosticSource: 3 MB of files the launcher then read at sign-in) for a download that may
    /// never come.
    /// </summary>
    public AIModelDownloader()
    {
        _artifactDownloader = new Lazy<IArtifactDownloader>(CreateSharedDownloader);
    }

    public AIModelDownloader(IArtifactDownloader artifactDownloader)
    {
        ArgumentNullException.ThrowIfNull(artifactDownloader);
        _artifactDownloader = new Lazy<IArtifactDownloader>(() => artifactDownloader);
    }

    // Not inlined into the constructor, where the client's type would load its assembly at start-up.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IArtifactDownloader CreateSharedDownloader() => new ArtifactDownloader(SharedHttpClient.Instance);

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

        await _artifactDownloader.Value
            .DownloadAsync(destinationDescriptor, destinationDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
