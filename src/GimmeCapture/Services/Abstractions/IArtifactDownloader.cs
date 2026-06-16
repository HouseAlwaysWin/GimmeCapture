using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Abstractions;

public interface IArtifactDownloader
{
    Task<string> DownloadAsync(
        ArtifactDescriptor descriptor,
        string destinationDirectory,
        IProgress<ArtifactDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
