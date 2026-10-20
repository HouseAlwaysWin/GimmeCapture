using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using Xunit;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The overlay awaits <see cref="RecordingService.StartAsync"/> on the UI thread, and StartAsync used to do all of its
/// native work before first yielding: loading FFmpeg if nothing had yet (seconds), opening the WASAPI loopback endpoint
/// (~60 ms, far longer for a device waking from idle), the WGC support check (~25-50 ms). Starting a recording froze
/// the overlay for that long — 1.8 s in the worst case measured.
///
/// This drives the real service on an STA thread that pumps Win32 messages, as Avalonia's UI thread does, and
/// measures how long each start holds it. Opt-in: it records the real screen and system audio, so it needs a Windows
/// desktop session with an audio endpoint — run it by hand when touching the recording start path.
/// </summary>
public class RecordingStartUiStallTests
{
    private const int Starts = 3;

    private readonly ITestOutputHelper _output;

    public RecordingStartUiStallTests(ITestOutputHelper output) => _output = output;

    [OptInFact("REC_STALL_PROBE=1")]
    public void StartingARecording_DoesNotBlockTheCallingUiThread()
    {
        var results = new List<StartResult>();
        Exception? failure = null;

        var ui = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.Run(new ProbeContext(results, _output));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Assert.True(ui.Join(TimeSpan.FromMinutes(2)), "the probe's UI thread did not finish");
        Assert.Null(failure);
        Assert.Equal(Starts, results.Count);

        foreach (var r in results)
        {
            _output.WriteLine($"start: started={r.Started} StartAsync returned after {r.SyncMs:F0} ms, "
                + $"start took {r.StartMs:F0} ms, longest UI-thread block {r.LongestBlockMs:F0} ms {r.Error}");
        }

        Assert.All(results, r => Assert.True(r.Started, "recording did not start: " + r.Error));
        // The first start also loads FFmpeg (no preload here), which used to happen right on the calling thread.
        Assert.All(results, r => Assert.True(r.SyncMs < 20, $"StartAsync held its caller for {r.SyncMs:F0} ms"));
        Assert.All(results, r => Assert.True(r.LongestBlockMs < 50, $"UI thread blocked {r.LongestBlockMs:F0} ms during a start"));
    }

    private sealed record StartResult(double SyncMs, double StartMs, double LongestBlockMs, bool Started, string Error);

    private sealed class ProbeContext : System.Windows.Forms.ApplicationContext
    {
        private readonly List<StartResult> _results;
        private readonly ITestOutputHelper _output;
        private readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 10 };
        private long _lastTick;
        private double _longestGapMs;

        public ProbeContext(List<StartResult> results, ITestOutputHelper output)
        {
            _results = results;
            _output = output;
            // WM_TIMER is the lowest-priority message, like the app's stall monitor: a late tick is a blocked thread.
            _heartbeat.Tick += (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                if (_lastTick != 0)
                {
                    _longestGapMs = Math.Max(_longestGapMs, Stopwatch.GetElapsedTime(_lastTick, now).TotalMilliseconds);
                }

                _lastTick = now;
            };

            // Start from inside the message loop, so the awaits below capture the pumping SynchronizationContext.
            var kick = new System.Windows.Forms.Timer { Interval = 50 };
            kick.Tick += (_, _) =>
            {
                kick.Stop();
                RunAsync();
            };
            kick.Start();
        }

        private async void RunAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "RecStall", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var settings = new AppSettingsService(root);
                var service = new RecordingService(new FFmpegDownloaderService(settings), settings);
                _heartbeat.Start();

                for (int i = 0; i < Starts; i++)
                {
                    await Task.Delay(300); // let the heartbeat settle between starts
                    // Reset only the maximum: the gap that contains the start begins at the last tick before it.
                    _longestGapMs = 0;

                    var sw = Stopwatch.StartNew();
                    var start = service.StartAsync(
                        new Rect(100, 100, 1280, 720), Path.Combine(root, $"start_{i}.mp4"), "mp4",
                        includeCursor: true, fps: 30, recordSystemAudio: true, recordMicrophone: false);
                    double syncMs = sw.Elapsed.TotalMilliseconds;
                    bool started = await start;
                    double startMs = sw.Elapsed.TotalMilliseconds;
                    await Task.Delay(150); // continuations right after the start still count

                    _results.Add(new StartResult(syncMs, startMs, _longestGapMs, started, service.LastStartError ?? ""));
                    await Task.Delay(700);
                    if (started)
                    {
                        await service.StopAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine("probe failed: " + ex);
            }
            finally
            {
                _heartbeat.Stop();
                try { Directory.Delete(root, true); } catch { /* best effort */ }
                ExitThread();
            }
        }
    }
}
