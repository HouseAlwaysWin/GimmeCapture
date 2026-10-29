using System;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    private readonly object _warmUpGate = new();
    private Task? _warmUp;

    /// <summary>
    /// Loads, while the user is still choosing what to record, what a run's first recording would otherwise load during
    /// its start: the FFmpeg libraries (about 50 ms), the recording encoder (a run's first NVENC open took 135-260 ms and
    /// every later one 80-100 ms), the system-audio endpoint and the answer to whether WGC works. The capture overlay
    /// calls this on entering Record mode. It runs once a run, on the thread pool, and never fails: a step that does
    /// not work here is done again by the start, which reports it.
    /// </summary>
    public Task WarmUpAsync()
    {
        lock (_warmUpGate)
        {
            return _warmUp ??= Task.Run(WarmUp);
        }
    }

    private void WarmUp()
    {
        var timings = new RecordingStartTimings("warmUp");
        try
        {
            if (!FFmpegRuntime.TryInitialize(out _))
            {
                return;
            }

            timings.Mark("ffmpeg");
            Step("encoderOpen", () =>
            {
                bool preferHardware = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly;
                timings.Note("encoder", LibavRecordingEncoder.WarmUpEncoder(ResolveRecordingCodec(), preferHardware));
            });

            if (OperatingSystem.IsWindows())
            {
                // Opening the endpoint and its capture client without starting anything: no stream plays or records.
                Step("audioEndpoint", () =>
                {
                    using var endpoint = _openSystemAudioEndpoint();
                    endpoint.CreateLoopbackCapture().Dispose();
                });
            }

            Step("wgcProbe", () => _ = WgcAvailable);
        }
        finally
        {
            AppLog.Information("Recording.WarmUp", timings.Finish());
        }

        void Step(string stage, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // No audio device, say: the start meets the same failure and handles it.
                AppLog.Debug("Recording.WarmUp", ex);
                timings.Note("failed", stage);
            }

            timings.Mark(stage);
        }
    }
}
