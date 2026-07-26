using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Outcome of a screenshot upload. <see cref="NotConfigured"/> means no Client-ID is set —
/// the caller should point the user at the settings page instead of showing a generic failure.</summary>
public sealed record ImageUploadResult(
    bool Success,
    string? Link,
    string? DeleteHash,
    string? Error,
    bool NotConfigured = false);

/// <summary>
/// Anonymous Imgur upload (https://api.imgur.com/3/image) authorized by the user's own Client-ID
/// (registered free at https://api.imgur.com/oauth2/addclient — the app ships none). Fail-fast like
/// every other HTTP service here: no retry, errors logged under "Upload.Imgur" and surfaced in the
/// result. The Client-ID is read per call so settings edits apply without restart.
/// </summary>
public sealed class ImgurUploadService : IImgurUploadService
{
    private const string UploadEndpoint = "https://api.imgur.com/3/image";
    // Imgur's documented cap for non-animated images.
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<string?> _clientIdProvider;
    private readonly HttpClient _httpClient;

    public ImgurUploadService(Func<string?> clientIdProvider, HttpClient? httpClient = null)
    {
        _clientIdProvider = clientIdProvider ?? throw new ArgumentNullException(nameof(clientIdProvider));
        _httpClient = httpClient ?? SharedHttpClient.Instance;
    }

    public async Task<ImageUploadResult> UploadPngAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);

        string clientId = _clientIdProvider()?.Trim() ?? string.Empty;
        if (clientId.Length == 0)
        {
            return new ImageUploadResult(false, null, null, "No Imgur Client-ID configured.", NotConfigured: true);
        }

        if (png.Length == 0 || png.Length > MaxImageBytes)
        {
            AppLog.Warning("Upload.Imgur.Size", $"Rejected payload of {png.Length} bytes.");
            return new ImageUploadResult(false, null, null, $"Image size {png.Length} bytes is outside Imgur's limit.");
        }

        try
        {
            AppLog.Information($"Upload.Imgur.Start.{png.Length}bytes");

            // The upload gets its own timeout: the shared client's global timeout is tuned for
            // hour-long model downloads and would let a dead upload hang the status forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(UploadTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint);
            // Per-request header — never DefaultRequestHeaders, the HttpClient is shared app-wide.
            request.Headers.Authorization = new AuthenticationHeaderValue("Client-ID", clientId);

            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(png);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(imageContent, "image", "screenshot.png");
            request.Content = content;

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warning("Upload.Imgur.Http", $"Status {(int)response.StatusCode}.");
                return new ImageUploadResult(false, null, null, $"Imgur returned HTTP {(int)response.StatusCode}.");
            }

            return ParseUploadResponse(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine caller cancellation — let it propagate
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning("Upload.Imgur.Timeout", $"Upload exceeded {UploadTimeout.TotalSeconds:F0}s.");
            return new ImageUploadResult(false, null, null, "Upload timed out.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Warning("Upload.Imgur.Network", ex);
            return new ImageUploadResult(false, null, null, "Network error during upload.");
        }
    }

    private static ImageUploadResult ParseUploadResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool success = root.TryGetProperty("success", out var successProp) && successProp.GetBoolean();
            if (success
                && root.TryGetProperty("data", out var data)
                && data.TryGetProperty("link", out var linkProp)
                && linkProp.GetString() is { Length: > 0 } link)
            {
                string? deleteHash = data.TryGetProperty("deletehash", out var hashProp) ? hashProp.GetString() : null;
                // Information level on purpose: the deletehash is the only way to delete an
                // anonymous upload later, so it must be recoverable from the log.
                AppLog.Information($"Upload.Imgur.Success.link={link}.deletehash={deleteHash}");
                return new ImageUploadResult(true, link, deleteHash, null);
            }

            AppLog.Warning("Upload.Imgur.Response", "Response missing success/data.link.");
            return new ImageUploadResult(false, null, null, "Imgur response did not contain a link.");
        }
        catch (JsonException ex)
        {
            AppLog.Warning("Upload.Imgur.Parse", ex);
            return new ImageUploadResult(false, null, null, "Could not parse the Imgur response.");
        }
    }
}
