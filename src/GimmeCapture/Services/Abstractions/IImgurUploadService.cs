using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Abstractions;

/// <summary>Uploads a PNG screenshot to an image host and returns the share link.</summary>
public interface IImgurUploadService
{
    Task<ImageUploadResult> UploadPngAsync(byte[] png, CancellationToken cancellationToken = default);
}
