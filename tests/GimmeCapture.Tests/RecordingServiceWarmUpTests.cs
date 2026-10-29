using System.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// The capture overlay warms the recorder up on entering Record mode, so a run's first recording does not load FFmpeg
/// and open its encoder for the first time while the user waits. Uses the real FFmpeg and libx264, which every CI job
/// provisions, and a stand-in audio endpoint that fails the way a machine with no audio device does.
/// </summary>
public sealed class RecordingServiceWarmUpTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "WarmUp", Guid.NewGuid().ToString("N"));

    public RecordingServiceWarmUpTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task WarmUp_RunsOnce_AndAStepThatFailsDoesNotFailIt()
    {
        var settings = new AppSettingsService(_folder);
        settings.Settings.VideoEncoderHint = VideoEncoderHint.SoftwareOnly;
        var service = new RecordingService(new FFmpegDownloaderService(settings), settings);
        int endpointsOpened = 0;
        service.UseSystemAudioEndpointForTest(() =>
        {
            Interlocked.Increment(ref endpointsOpened);
            throw new InvalidOperationException("no audio device");
        });

        Task first = service.WarmUpAsync();
        Task second = service.WarmUpAsync();
        await first;

        Assert.Same(first, second);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(FFmpegRuntime.IsInitialized);
        // WASAPI is Windows-only; Linux records system audio through PulseAudio and warms nothing up for it.
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, endpointsOpened);
    }
}
