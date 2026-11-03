using System.Diagnostics;
using Avalonia;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using NAudio.Wave;

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

    [Fact]
    public async Task ARecordingWithSound_HasItEncodedWhileItRecords_PausedOrNot()
    {
        // The sound is encoded to AAC as the recording goes (RecordingSoundEncoder), so the stop only finishes it: the
        // first stretch starts the encoder, the resume hands it the next stretch's WAV, each pause's stop ends one. The
        // system audio comes from a stand-in loopback playing a tone, as CI has no audio device.
        var settings = new AppSettingsService(_folder);
        settings.Settings.VideoEncoderHint = VideoEncoderHint.SoftwareOnly;
        var service = new RecordingService(new FFmpegDownloaderService(settings), settings);
        service.UseSystemAudioEndpointForTest(() => new ToneEndpoint());
        string output = Path.Combine(_folder, "sound.mp4");

        bool started = await service.StartAsync(
            new Rect(0, 0, 320, 240), output, "mp4", includeCursor: false, fps: 15, recordSystemAudio: true);
        Assert.True(started, service.LastStartError);
        await OwnThread.Delay(TimeSpan.FromSeconds(1.5));
        await service.PauseAsync();
        await service.ResumeAsync();
        await OwnThread.Delay(TimeSpan.FromSeconds(1.5));
        await service.StopAsync();

        string? timings = service.LastFinalizeTimings;
        var extents = RecordingAvSyncTests.ProbeExtents(service.OutputFilePath!);
        var video = Assert.Single(extents, e => e.Type == AVMediaType.AVMEDIA_TYPE_VIDEO);
        var audio = Assert.Single(extents, e => e.Type == AVMediaType.AVMEDIA_TYPE_AUDIO);
        Assert.Contains("segments=2 ", timings);
        Assert.Contains("sound=recorded", timings);
        Assert.True(
            Math.Abs(audio.LengthSeconds - video.LengthSeconds) <= 0.15,
            $"video {video.LengthSeconds:F3} s, audio {audio.LengthSeconds:F3} s; {timings}");
    }

    // A render endpoint whose loopback hands over a 440 Hz tone, as much as the time since its last burst, every 10 ms
    // or so from one thread, as WASAPI does.
    private sealed class ToneEndpoint : RecordingService.ISystemAudioEndpoint
    {
        public IWaveIn CreateLoopbackCapture() => new ToneCapture();

        public IDisposable StartSilence(WaveFormat format) => new Silence();

        public void Dispose()
        {
        }

        private sealed class Silence : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class ToneCapture : IWaveIn
    {
        private Thread? _thread;
        private volatile bool _running;

        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public event EventHandler<WaveInEventArgs>? DataAvailable;

        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public void StartRecording()
        {
            _running = true;
            _thread = new Thread(Deliver) { IsBackground = true };
            _thread.Start();
        }

        public void StopRecording()
        {
            _running = false;
            _thread?.Join();
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }

        public void Dispose()
        {
            _running = false;
            _thread?.Join();
        }

        private void Deliver()
        {
            var clock = Stopwatch.StartNew();
            long delivered = 0;
            while (_running)
            {
                Thread.Sleep(10);
                long due = (long)(clock.Elapsed.TotalSeconds * 48_000);
                int frames = (int)(due - delivered);
                var buffer = new byte[frames * 8];
                for (int i = 0; i < frames; i++)
                {
                    float sample = (float)(0.3 * Math.Sin(2 * Math.PI * 440 * (delivered + i) / 48_000));
                    BitConverter.TryWriteBytes(buffer.AsSpan(i * 8), sample);
                    BitConverter.TryWriteBytes(buffer.AsSpan((i * 8) + 4), sample);
                }

                delivered = due;
                DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, buffer.Length));
            }
        }
    }
}
