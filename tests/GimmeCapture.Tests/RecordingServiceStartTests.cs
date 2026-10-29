using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

/// <summary>
/// Starts real recordings through <see cref="RecordingService"/>: a corner of the desktop, no audio, the software
/// encoder. Like the gdigrab session tests it records without showing anything, so it runs with the other Windows tests.
/// </summary>
public sealed class RecordingServiceStartTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "RecordingStart", Guid.NewGuid().ToString("N"));

    public RecordingServiceStartTests() => Directory.CreateDirectory(_folder);

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
    public async Task ARegionRecording_DoesNotAskWhetherWgcWorks()
    {
        // Only a picked window records through WGC, yet every start asked GraphicsCaptureSession.IsSupported(): 16 ms a
        // recording, 45 ms on a run's first.
        var settings = new AppSettingsService(_folder);
        settings.Settings.VideoEncoderHint = VideoEncoderHint.SoftwareOnly;
        var service = new RecordingService(new FFmpegDownloaderService(settings), settings);

        bool started = await service.StartAsync(
            new Rect(0, 0, 320, 240), Path.Combine(_folder, "region.mp4"), "mp4", includeCursor: false, fps: 15);
        string? timings = service.LastStartTimings;
        if (started)
        {
            await service.StopAsync();
        }

        Assert.True(started, service.LastStartError);
        Assert.NotNull(timings);
        Assert.DoesNotContain("wgcProbe", timings);
        Assert.Contains(" video=", timings);
        Assert.Contains("encoder=libx264", timings);
        Assert.EndsWith("started=yes", timings);
    }
}
