using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Floating;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// How long a smart-selection click on a pin holds the UI thread. Every click used to copy the whole pin there (about
/// 10 ms and 37 MB at 3840x2400) for SAM2 to find unchanged, then colour in the mask, draw the point markers and hand
/// the preview to Avalonia (about 18 ms): about 30 ms of a 4K click on a quiet machine. It is about 2 ms now, most of
/// it freeing the previous click's preview and mask.
///
/// This drives the real view model, with <see cref="FakeSam2Service"/> in place of the model, on a thread that runs
/// what is posted to it one item at a time as Avalonia's UI thread does, and times every item a click runs there.
/// Opt-in: it measures, so run it by hand, on a quiet machine, when touching the click path.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public sealed class SmartSelectionClickUiStallTests
{
    private const int WarmUpClicks = 2;
    private const int Clicks = 11;

    private readonly ITestOutputHelper _output;

    public SmartSelectionClickUiStallTests(ITestOutputHelper output) => _output = output;

    [OptInFact("SAM2_CLICK_PROBE=1")]
    public void AClick_HoldsTheUiThreadBriefly()
    {
        AvaloniaRenderer.EnsureRegistered();

        var largest = MeasureClicks(3840, 2400);
        MeasureClicks(1920, 1200);

        Assert.True(Median(largest.Select(c => c.LongestMs)) < 5, "a 4K click held the UI thread for 5 ms or more at a time");
        Assert.True(Median(largest.Select(c => c.TotalMs)) < 10, "a 4K click held the UI thread for 10 ms or more in all");
    }

    private List<ClickTiming> MeasureClicks(int width, int height)
    {
        string directory = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "Sam2ClickProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var loop = new TimedLoop();
        try
        {
            string encoder = Path.Combine(directory, "encoder.onnx");
            string decoder = Path.Combine(directory, "decoder.onnx");
            File.WriteAllBytes(encoder, Array.Empty<byte>());
            File.WriteAllBytes(decoder, Array.Empty<byte>());

            var settings = new Mock<IAppSettingsService>();
            settings.Setup(s => s.Settings).Returns(new AppSettings());
            var paths = new Mock<AIPathService>(settings.Object);
            paths.Setup(p => p.GetSAM2Paths(It.IsAny<SAM2Variant>())).Returns((encoder, decoder));
            var ai = new AIResourceService(settings.Object, paths.Object, new Mock<AIModelDownloader>().Object);

            // About 41% selected, as a real SAM2 Tiny mask of a photo pin was: an ellipse 0.72 of the pin each way.
            using var mask = TestBitmaps.Create(width, height, SKColorType.Gray8, SKAlphaType.Opaque, (x, y) =>
            {
                double dx = (x - (width / 2.0)) / (0.36 * width);
                double dy = (y - (height / 2.0)) / (0.36 * height);
                return (dx * dx) + (dy * dy) <= 1 ? (byte)255 : (byte)0;
            });
            var sam2 = new FakeSam2Service(mask);
            using var image = AvaloniaRenderer.Create(width, height, (x, y) => 0xFF000000u | (TestBitmaps.Noise(x, y) & 0x00FFFFFFu));

            FloatingImageViewModel? pin = null;
            loop.Measure(() =>
            {
                pin = new FloatingImageViewModel(
                    image, width, height, Colors.Red, 2.0, false, false, new Mock<IClipboardService>().Object, ai,
                    new SAM2RuntimeService(paths.Object), settings.Object, paths.Object, new ResourceQueueService());
                pin.SetSam2ServiceForTesting(sam2);
                pin.CurrentTool = FloatingTool.PointRemoval;
                return Task.CompletedTask;
            });
            Assert.True(sam2.FirstPrepare.Wait(TimeSpan.FromSeconds(30)), "the session never prepared the pin");

            var timings = new List<ClickTiming>();
            // Every click's preview, so that two builds can be shown to draw the same ones.
            using var previews = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int i = 0; i < WarmUpClicks + Clicks; i++)
            {
                // Inside the selection, a different spot each time; every third one takes from the selection.
                double x = width * (0.35 + (0.03 * (i % 10)));
                double y = height * (0.4 + (0.02 * (i % 10)));
                bool isPositive = i % 3 != 2;
                var stopwatch = Stopwatch.StartNew();
                double[] items = loop.Measure(() => pin!.HandlePointClickAsync(x, y, isPositive));
                double wallMs = stopwatch.Elapsed.TotalMilliseconds;
                if (i >= WarmUpClicks)
                {
                    timings.Add(new ClickTiming(items.Sum(), items.Max(), items, wallMs));
                }

                previews.AppendData(MemoryMarshal.AsBytes(AvaloniaRenderer.ReadPixels(pin!.InteractiveMask!).AsSpan()));
            }

            loop.Measure(() =>
            {
                pin!.Dispose();
                return Task.CompletedTask;
            });

            Assert.Equal(WarmUpClicks + Clicks, sam2.MaskRequests.Count);
            // Every click runs the same items in the same order: the click itself, then each continuation.
            int itemCount = timings[0].Items.Length;
            Assert.All(timings, t => Assert.Equal(itemCount, t.Items.Length));
            string eachItem = string.Join(", ", Enumerable.Range(0, itemCount).Select(n => Describe(timings.Select(t => t.Items[n]))));
            _output.WriteLine(
                $"{width}x{height}, {Clicks} clicks, ms (median, min-max): UI thread in all {Describe(timings.Select(t => t.TotalMs))}, " +
                $"longest at a time {Describe(timings.Select(t => t.LongestMs))}, click to preview {Describe(timings.Select(t => t.WallMs))}; " +
                $"each of the {itemCount} items a click ran there: {eachItem}; images handed to SAM2: {sam2.PrepareCount}; " +
                $"previews SHA-256 {Convert.ToHexString(previews.GetHashAndReset())[..16]}");
            return timings;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Describe(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return $"{Median(sorted):F2} ({sorted[0]:F2}-{sorted[^1]:F2})";
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private sealed record ClickTiming(double TotalMs, double LongestMs, double[] Items, double WallMs);

    /// <summary>
    /// A thread that runs what is posted to it one item at a time, as a UI thread does, and times each item. Awaits on
    /// it come back to it, as they come back to Avalonia's UI thread.
    /// </summary>
    private sealed class TimedLoop : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly List<double> _itemMs = new();
        private readonly Thread _thread;

        public TimedLoop()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Probe UI thread" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>
        /// Starts <paramref name="start"/> on the loop and waits for the task it returns. Gives the time, in ms, of each
        /// item the loop ran meanwhile, the start included.
        /// </summary>
        public double[] Measure(Func<Task> start)
        {
            lock (_itemMs)
            {
                _itemMs.Clear();
            }

            Exception? failure = null;
            using var done = new ManualResetEventSlim();
            Post(_ =>
            {
                try
                {
                    start().ContinueWith(
                        task =>
                        {
                            failure = task.Exception;
                            done.Set();
                        },
                        TaskContinuationOptions.ExecuteSynchronously);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    done.Set();
                }
            }, null);
            if (!done.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("the probe's UI thread did not finish a click");
            }

            // The item that finished the task is still being timed: read the times from the next one.
            double[] items = Array.Empty<double>();
            using var read = new ManualResetEventSlim();
            Post(_ =>
            {
                lock (_itemMs)
                {
                    items = _itemMs.ToArray();
                }

                read.Set();
            }, null);
            read.Wait();
            if (failure != null)
            {
                throw new InvalidOperationException("a click failed on the probe's UI thread", failure);
            }

            return items;
        }

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                long started = Stopwatch.GetTimestamp();
                callback(state);
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                lock (_itemMs)
                {
                    _itemMs.Add(ms);
                }
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
            _queue.Dispose();
        }
    }
}
