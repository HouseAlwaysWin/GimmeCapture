using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using ReactiveUI.Reactive;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A 4K pin rotated three times, which keeps each picture for undo, with smart selection's model leased, then closed by
/// its close button (PIN_CLOSE_PROBE=command) or as Alt+F4 closes it, the window alone (PIN_CLOSE_PROBE=window). Opt-in:
/// it sets up an Avalonia application, which a process can do only once, and reads the private bytes of the whole test
/// process, so run each way on its own.
/// </summary>
public sealed class PinCloseMemoryTests : IDisposable
{
    private const int PictureWidth = 3840;
    private const int PictureHeight = 2160;
    private const int Rotations = 3;
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "PinClose", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public PinCloseMemoryTests(ITestOutputHelper output)
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
        catch (IOException)
        {
            // best effort
        }
    }

    [OptInFact("PIN_CLOSE_PROBE")]
    public void AClosedPin_LetsGoOfItsPictures_AndOfSmartSelection()
    {
        bool byCommand = Environment.GetEnvironmentVariable("PIN_CLOSE_PROBE") == "command";
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        IScheduler testScheduler = RxSchedulers.MainThreadScheduler;
        RxSchedulers.MainThreadScheduler = AvaloniaMainThreadScheduler.Instance;
        try
        {
            var settings = new AppSettingsService(_folder);
            var paths = new AIPathService(settings);
            var ai = new AIResourceService(settings, paths, new AIModelDownloader());
            var sam2 = new SAM2RuntimeService(paths);
            long before = PrivateMegabytes();
            long heapBefore = GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024);

            var (pin, open) = OpenRotateAndClose(byCommand, settings, paths, ai, sam2);
            long closed = PrivateMegabytes();
            bool leased = sam2.HasActiveLeases;
            int heldForUndo = ((System.Collections.ICollection)typeof(BitmapHistoryAction)
                .GetField("_bitmapRefCounts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Count;

            _output.WriteLine(
                $"closed by {(byCommand ? "its close button" : "the window alone")}: private bytes {before} MB before the pin, " +
                $"+{open - before} MB open, +{closed - before} MB closed, of which the .NET heap " +
                $"+{(GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024)) - heapBefore} MB; " +
                $"view model collected: {!pin.IsAlive}; " +
                $"smart selection still leased: {leased}; pictures held for undo {heldForUndo}");
            Assert.False(leased);
            Assert.Equal(0, heldForUndo);
            Assert.False(pin.IsAlive);
            // 6-7 MB stays either way. It was 38-40, 33 of it on the .NET heap: the 32 MiB array a rotation copied the
            // picture through, rented from the shared pool, which kept it. The pictures kept for undo were 125 MB more.
            Assert.InRange(closed - before, long.MinValue, 25);
        }
        finally
        {
            RxSchedulers.MainThreadScheduler = testScheduler;
        }
    }

    // Kept out of the test so nothing on its stack holds the pin once it is closed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Pin, long OpenMegabytes) OpenRotateAndClose(
        bool byCommand, AppSettingsService settings, AIPathService paths, AIResourceService ai, SAM2RuntimeService sam2)
    {
        var picture = new WriteableBitmap(
            new PixelSize(PictureWidth, PictureHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var vm = new FloatingImageViewModel(
            picture, PictureWidth / 2, PictureHeight / 2, Colors.Red, 2.0, false, false, new Mock<IClipboardService>().Object,
            ai, sam2, settings, paths, new ResourceQueueService());
        var window = new FloatingImageWindow { DataContext = vm, Position = new PixelPoint(100, 100) };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(300));

        for (int i = 1; i <= Rotations; i++)
        {
            vm.RotateRightCommand.Execute().Subscribe();
            PumpUntil(() => vm.EditorState.HistoryStack.Count == i, TimeSpan.FromSeconds(10));
        }

        // The smart selection tool, picked: its model stays loaded while a pin holds a lease.
        vm.EnsureSam2Lease();
        long open = PrivateMegabytes();

        if (byCommand)
        {
            vm.CloseCommand.Execute().Subscribe();
        }
        else
        {
            window.Close();
        }

        Pump(TimeSpan.FromMilliseconds(500));
        return (new WeakReference(vm), open);
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var giveUp = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(giveUp.Elapsed < timeout, "The pin did not get there in time.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static void Pump(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        while (watch.Elapsed < duration);
    }

    private static long PrivateMegabytes()
    {
        Dispatcher.UIThread.RunJobs();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024 * 1024);
    }
}
