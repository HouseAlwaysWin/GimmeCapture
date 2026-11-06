using System.Collections.Concurrent;
using System.Threading;
using GimmeCapture.Services.Core.Media;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Record mode's warm-up and then a recording through the real <see cref="RecordingService"/>, as the capture overlay
/// runs them, read from the log: the recording should grab its region through Desktop Duplication. On a laptop whose
/// display hangs off the main chip, with an NVIDIA card beside it, the warm-up's cuInit used to put the whole process on
/// the NVIDIA card, which DXGI then listed the display under, and the duplication failed there ("Selected output not
/// supported"), so every recording fell back to gdigrab (DisplayAdapterOrder). Opt-in (DESKTOP_DUPLICATION_PROBE=1):
/// it needs that hardware, records the real screen, and only means something run alone, in a process of its own,
/// before anything else in it has touched DXGI or CUDA.
/// </summary>
[Collection(ScreenRecordingCollection.Name)]
public sealed class RecordingDesktopDuplicationProbeTests(ITestOutputHelper output)
{
    [OptInFact("DESKTOP_DUPLICATION_PROBE=1")]
    public void ARecordingAfterRecordModesWarmUp_GrabsThroughDesktopDuplication()
    {
        var logged = new ConcurrentQueue<string>();
        var previousLogger = Serilog.Log.Logger;
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(new Sink(logged)).CreateLogger();
        string root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "WarmUpThenRecord", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? outcome = null;
            // From a thread that pumps messages, as the overlay's UI thread does (RecordingAvSyncTests).
            var ui = new Thread(() => System.Windows.Forms.Application.Run(new RecordContext(root, result => outcome = result)));
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            Assert.True(ui.Join(TimeSpan.FromMinutes(2)), "the recording thread did not finish");

            output.WriteLine(outcome);
            foreach (string line in logged.Where(l => l.Contains("WarmUp") || l.Contains("Duplication") || l.Contains("Capture source")))
            {
                output.WriteLine(line);
            }

            Assert.StartsWith("started", outcome);
            Assert.Contains(logged, line => line.Contains("Capture source: ddagrab"));
        }
        finally
        {
            Serilog.Log.Logger = previousLogger;
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    private sealed class RecordContext : System.Windows.Forms.ApplicationContext
    {
        public RecordContext(string root, Action<string> done)
        {
            // Started from inside the message loop, so the awaits come back to it.
            var kick = new System.Windows.Forms.Timer { Interval = 50 };
            kick.Tick += async (_, _) =>
            {
                kick.Stop();
                try
                {
                    var settings = new AppSettingsService(root);
                    var service = new RecordingService(new FFmpegDownloaderService(settings), settings);
                    await service.WarmUpAsync();
                    string file = Path.Combine(root, "recording.mp4");
                    bool started = await service.StartAsync(
                        new Avalonia.Rect(100, 100, 1280, 720), file, "mp4",
                        includeCursor: true, fps: 30, recordSystemAudio: false, recordMicrophone: false);
                    if (started)
                    {
                        await Task.Delay(2000);
                        await service.StopAsync();
                    }

                    done(started ? $"started, {service.LastStartTimings}" : $"did not start: {service.LastStartError}");
                }
                catch (Exception ex)
                {
                    done("failed: " + ex);
                }
                finally
                {
                    ExitThread();
                }
            };
            kick.Start();
        }
    }

    private sealed class Sink(ConcurrentQueue<string> lines) : Serilog.Core.ILogEventSink
    {
        public void Emit(Serilog.Events.LogEvent logEvent) => lines.Enqueue(logEvent.RenderMessage());
    }
}
