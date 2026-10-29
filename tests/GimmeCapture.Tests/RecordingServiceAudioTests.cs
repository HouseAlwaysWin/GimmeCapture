using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// System audio is recorded through a WASAPI loopback capture of the render endpoint, which hands over nothing at all
/// while no stream renders there, so a stretch where nothing played went missing from the recording. RecordingService
/// keeps a stream of its own rendering silence to that endpoint for as long as the capture runs. These check what opens
/// and closes on a stand-in endpoint, and in which order, since CI has no audio device; the opt-in probe at the end
/// runs on the real one.
/// </summary>
public sealed class RecordingServiceAudioTests : IDisposable
{
    // AUDCLNT_E_DEVICE_IN_USE: another program holds the endpoint in exclusive mode.
    private const int DeviceInUse = unchecked((int)0x8889000A);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "SystemAudio", Guid.NewGuid().ToString("N"));
    private readonly List<string> _events = new();
    private readonly ITestOutputHelper _output;

    public RecordingServiceAudioTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // %TEMP% clean-up gets it later.
        }
    }

    [WindowsFact]
    public void TheSilentStream_StartsBeforeTheCapture_InTheCapturesFormat()
    {
        var endpoint = new StandInEndpoint(_events);
        var service = ServiceOn(endpoint);

        Assert.True(service.TryStartAudioCapture(SegmentPath(0)));

        Assert.Equal(["capture opened", "silence started", "capture started"], _events);
        Assert.Equal(endpoint.Capture!.WaveFormat, endpoint.SilenceFormat);
        service.StopAudioCapture();
    }

    [WindowsFact]
    public void TheSilentStream_StopsAfterTheCapture_ThenTheEndpointIsReleased()
    {
        var service = ServiceOn(new StandInEndpoint(_events));
        Assert.True(service.TryStartAudioCapture(SegmentPath(0)));
        _events.Clear();

        service.StopAudioCapture();

        Assert.Equal(["capture stopped", "capture disposed", "silence stopped", "endpoint released"], _events);
    }

    [WindowsFact]
    public void SystemAudio_IsStillRecorded_WhenTheSilentStreamCannotOpen()
    {
        var endpoint = new StandInEndpoint(_events) { SilenceFailure = new COMException("In use.", DeviceInUse) };
        var service = ServiceOn(endpoint);

        Assert.True(service.TryStartAudioCapture(SegmentPath(0)));
        Assert.Equal(["capture opened", "capture started"], _events);

        service.StopAudioCapture();
        Assert.Equal(
            ["capture opened", "capture started", "capture stopped", "capture disposed", "endpoint released"],
            _events);
    }

    [WindowsFact]
    public void TheSilentStream_IsClosed_WhenTheCaptureFailsToStart()
    {
        var endpoint = new StandInEndpoint(_events) { CaptureStartFailure = new COMException("In use.", DeviceInUse) };
        var service = ServiceOn(endpoint);

        Assert.False(service.TryStartAudioCapture(SegmentPath(0)));

        Assert.Equal(
            [
                "capture opened", "silence started",
                "capture stopped", "capture disposed", "silence stopped", "endpoint released",
            ],
            _events);
    }

    [WindowsFact]
    public void EachSegment_OpensASilentStreamOfItsOwn_AndClosesIt()
    {
        // A pause closes the segment's audio, and a resume opens it again the way a start does.
        int opened = 0;
        var service = new RecordingService(new FFmpegDownloaderService());
        service.UseSystemAudioEndpointForTest(() =>
        {
            opened++;
            return new StandInEndpoint(_events);
        });

        Assert.True(service.TryStartAudioCapture(SegmentPath(0)));
        service.StopAudioCapture();
        Assert.True(service.TryStartAudioCapture(SegmentPath(1)));
        service.StopAudioCapture();

        Assert.Equal(2, opened);
        string[] segment =
        [
            "capture opened", "silence started", "capture started",
            "capture stopped", "capture disposed", "silence stopped", "endpoint released",
        ];
        Assert.Equal([.. segment, .. segment], _events);
    }

    // The fix on the real default endpoint: with the silent stream open, the loopback hands over audio for the whole
    // capture, whether or not anything else plays. Opt-in, as it needs a Windows audio endpoint. It renders zeros only.
    [OptInFact("LOOPBACK_PROBE=1")]
    public void TheSilentStream_KeepsTheRealLoopbackDelivering()
    {
        using var endpoint = RecordingService.WasapiSystemAudioEndpoint.OpenDefault();

        double without = CapturedShare(endpoint, withSilence: false);
        double with = CapturedShare(endpoint, withSilence: true);
        _output.WriteLine($"audio for {without:P1} of the capture without the silent stream, {with:P1} with it");

        Assert.True(with >= 0.9, $"With the silent stream the loopback delivered audio for only {with:P1} of the capture.");
    }

    // How much of a 3 s loopback capture came back as audio, as a share of the time it ran.
    private static double CapturedShare(RecordingService.ISystemAudioEndpoint endpoint, bool withSilence)
    {
        using var capture = endpoint.CreateLoopbackCapture();
        using IDisposable? silence = withSilence ? endpoint.StartSilence(capture.WaveFormat) : null;
        using var stopped = new ManualResetEventSlim();
        long bytes = 0;
        capture.DataAvailable += (_, e) => Interlocked.Add(ref bytes, e.BytesRecorded);
        capture.RecordingStopped += (_, _) => stopped.Set();

        var clock = Stopwatch.StartNew();
        capture.StartRecording();
        Thread.Sleep(TimeSpan.FromSeconds(3));
        capture.StopRecording();
        Assert.True(stopped.Wait(TimeSpan.FromSeconds(5)), "the loopback capture did not stop");

        return Interlocked.Read(ref bytes) / (clock.Elapsed.TotalSeconds * capture.WaveFormat.AverageBytesPerSecond);
    }

    private RecordingService ServiceOn(StandInEndpoint endpoint)
    {
        var service = new RecordingService(new FFmpegDownloaderService());
        service.UseSystemAudioEndpointForTest(() => endpoint);
        return service;
    }

    private string SegmentPath(int segment) => Path.Combine(_folder, $"segment_{segment}.wav");

    // Writes down what is opened, started, stopped and released on it, in order.
    private sealed class StandInEndpoint(List<string> events) : RecordingService.ISystemAudioEndpoint
    {
        public Exception? SilenceFailure { get; init; }

        public Exception? CaptureStartFailure { get; init; }

        public StandInCapture? Capture { get; private set; }

        public WaveFormat? SilenceFormat { get; private set; }

        public IWaveIn CreateLoopbackCapture()
        {
            events.Add("capture opened");
            Capture = new StandInCapture(events, CaptureStartFailure);
            return Capture;
        }

        public IDisposable StartSilence(WaveFormat format)
        {
            if (SilenceFailure != null)
            {
                throw SilenceFailure;
            }

            SilenceFormat = format;
            events.Add("silence started");
            return new Stop(() => events.Add("silence stopped"));
        }

        public void Dispose() => events.Add("endpoint released");
    }

    private sealed class StandInCapture(List<string> events, Exception? startFailure) : IWaveIn
    {
        // The mix format of a typical shared-mode endpoint.
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }

        public event EventHandler<StoppedEventArgs>? RecordingStopped { add { } remove { } }

        public void StartRecording()
        {
            if (startFailure != null)
            {
                throw startFailure;
            }

            events.Add("capture started");
        }

        public void StopRecording() => events.Add("capture stopped");

        public void Dispose() => events.Add("capture disposed");
    }

    private sealed class Stop(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
