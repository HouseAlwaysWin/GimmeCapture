using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Screenshot upload handoff. The snip overlay closes immediately after capturing; the upload
/// itself runs here (app lifetime) fire-and-forget, so the fullscreen overlay never blocks on a
/// network call and completion can still surface through the status/toast pipeline.
/// </summary>
public partial class MainWindowViewModel
{
    private ImgurUploadService? _imgurUpload;
    private IImgurUploadService? _imgurUploadOverride;
    private int _imgurUploadInFlight;

    // Lazily constructed instead of going through the dependencies record: the service's only
    // dependency is the live Client-ID setting, and it carries its own HttpClient test seam.
    public IImgurUploadService ImgurUpload =>
        _imgurUploadOverride ?? (_imgurUpload ??= new ImgurUploadService(() => ImgurClientId));

    // Test seam: lets a test drive the upload handoff without a real HTTP request. Production never
    // sets this, so the lazy real service above is unaffected.
    internal void SetImgurUploadServiceForTest(IImgurUploadService service) => _imgurUploadOverride = service;

    /// <summary>Uploads PNG bytes and copies the returned link via <paramref name="copyTextToClipboard"/>
    /// (returns whether the clipboard write succeeded). A second call while one upload is in flight is
    /// refused (status only, no queueing).</summary>
    public async Task RunImgurUploadAsync(byte[] png, Func<string, Task<bool>> copyTextToClipboard)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(copyTextToClipboard);

        if (Interlocked.CompareExchange(ref _imgurUploadInFlight, 1, 0) != 0)
        {
            SetStatus("StatusUploading");
            return;
        }

        try
        {
            SetStatus("StatusUploading");
            var result = await ImgurUpload.UploadPngAsync(png);
            if (result.Success && result.Link is { Length: > 0 } link)
            {
                // On clipboard failure the link is still recoverable from the Upload.Imgur log line.
                bool copied = await copyTextToClipboard(link);
                SetStatus(copied ? "StatusUploadedLinkCopied" : "StatusUploadFailed");
            }
            else if (result.NotConfigured)
            {
                SetStatus("StatusImgurClientIdMissing");
            }
            else
            {
                SetStatus("StatusUploadFailed");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Upload.Imgur.Unexpected", ex);
            SetStatus("StatusUploadFailed");
        }
        finally
        {
            Interlocked.Exchange(ref _imgurUploadInFlight, 0);
        }
    }
}
