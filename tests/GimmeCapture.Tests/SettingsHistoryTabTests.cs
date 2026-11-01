using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main.Tabs;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The History tab with the history full: 300 entries, each with a 320x180 thumbnail like those the app writes. Opt-in
/// (HISTORY_TAB_PROBE=1): it sets up an Avalonia application, which a process can do only once, writes the entries
/// into the test run's data folder and prints what the thumbnails held.
/// </summary>
public sealed class SettingsHistoryTabTests
{
    private const int Entries = 300;
    private readonly ITestOutputHelper _output;

    public SettingsHistoryTabTests(ITestOutputHelper output) => _output = output;

    [OptInFact("HISTORY_TAB_PROBE=1")]
    public void TheTab_DecodesTheThumbnailsOnScreen_AndLetsThemGoWhenLeft()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        WriteHistory();

        var vm = new MainWindowViewModel();
        PumpUntil(() => vm.InitialSettingsLoadTask.IsCompleted, TimeSpan.FromSeconds(30));
        long before = PrivateMegabytes();

        var tab = new SettingsHistoryTab { DataContext = vm };
        var window = new Window { Content = tab, Width = 1100, Height = 900 };
        window.Show();
        int decoded = PumpUntilSettled(() => vm.HistoryItems.Count(i => i.HasThumbnail));
        long open = PrivateMegabytes();

        // Scrolled to the end, the cards scrolled past let theirs go, and the last ones show theirs.
        var scroller = tab.FindControl<ScrollViewer>("HistoryScroller")!;
        int mostDecoded = 0;
        for (double y = 0; y <= scroller.Extent.Height; y += 180)
        {
            scroller.Offset = new Vector(0, y);
            Dispatcher.UIThread.RunJobs();
            mostDecoded = Math.Max(mostDecoded, vm.HistoryItems.Count(i => i.HasThumbnail));
        }

        scroller.Offset = new Vector(0, scroller.Extent.Height);
        int atTheEnd = PumpUntilSettled(() => vm.HistoryItems.Count(i => i.HasThumbnail));
        bool lastShown = vm.HistoryItems[^1].HasThumbnail;

        // Leaving the tab, as picking another settings tab does, takes it out of the window; coming back shows the
        // cards it kept.
        window.Content = null;
        int kept = PumpUntilSettled(() => vm.HistoryItems.Count(i => i.HasThumbnail));
        long left = PrivateMegabytes();
        window.Content = tab;
        int back = PumpUntilSettled(() => vm.HistoryItems.Count(i => i.HasThumbnail));
        window.Close();
        int closed = PumpUntilSettled(() => vm.HistoryItems.Count(i => i.HasThumbnail));

        _output.WriteLine(
            $"{vm.HistoryItems.Count} entries listed; thumbnails decoded with the tab open {decoded}, at most {mostDecoded} " +
            $"while scrolling, {atTheEnd} at the end, after leaving the tab {kept}, back on it {back}, the window closed " +
            $"{closed}; private bytes {before} MB before, {open} MB with the tab open, {left} MB after leaving it.");
        Assert.Equal(Entries, vm.HistoryItems.Count);
        Assert.InRange(decoded, 1, 40);
        Assert.InRange(mostDecoded, 1, 60);
        Assert.InRange(atTheEnd, 1, 40);
        Assert.True(lastShown, "The last card shows no thumbnail.");
        Assert.Equal(0, kept);
        Assert.InRange(back, 1, 40);
        Assert.Equal(0, closed);
    }

    private static void WriteHistory()
    {
        string historyDir = Path.Combine(AppStoragePaths.GetRootDirectory(), "history");
        string thumbsDir = Path.Combine(historyDir, "thumbs");
        string capturesDir = Path.Combine(historyDir, "captures");
        Directory.CreateDirectory(thumbsDir);
        Directory.CreateDirectory(capturesDir);

        var items = new List<CaptureHistoryItem>();
        using var bitmap = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(bitmap);
        var start = DateTime.UtcNow.AddDays(-1);
        for (int i = 0; i < Entries; i++)
        {
            string id = i.ToString("D4");
            canvas.Clear(new SKColor((byte)(i * 7), (byte)(i * 13), (byte)(i * 29)));
            using (var image = SKImage.FromBitmap(bitmap))
            using (var png = image.Encode(SKEncodedImageFormat.Png, 90))
            {
                File.WriteAllBytes(Path.Combine(thumbsDir, id + ".png"), png.ToArray());
            }

            string capture = Path.Combine(capturesDir, $"capture {id}.png");
            File.WriteAllBytes(capture, [0]);
            items.Add(new CaptureHistoryItem
            {
                Id = id,
                FilePath = capture,
                ThumbnailPath = Path.Combine(thumbsDir, id + ".png"),
                Kind = CaptureHistoryKind.Image,
                CapturedAtUtc = start.AddMinutes(i),
                Width = 1920,
                Height = 1080,
            });
        }

        File.WriteAllText(Path.Combine(historyDir, "history.json"), JsonSerializer.Serialize(items));
    }

    // The thumbnails decode on the pool and land on the UI thread: pump until the count has not moved for a second.
    private static int PumpUntilSettled(Func<int> count)
    {
        int last = -1;
        var settledSince = Stopwatch.StartNew();
        var giveUp = Stopwatch.StartNew();
        while (giveUp.Elapsed < TimeSpan.FromSeconds(30))
        {
            Dispatcher.UIThread.RunJobs();
            int now = count();
            if (now != last)
            {
                last = now;
                settledSince.Restart();
            }
            else if (settledSince.Elapsed > TimeSpan.FromSeconds(1))
            {
                break;
            }

            Thread.Sleep(10);
        }

        return last;
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var giveUp = Stopwatch.StartNew();
        while (!done() && giveUp.Elapsed < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static long PrivateMegabytes()
    {
        Dispatcher.UIThread.RunJobs();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / (1024 * 1024);
    }
}
