using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Views.Controls;
using ReactiveUI.Reactive;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Six mosaics and blurs on a 4K picture, shown the way a pin shows them, then the pin resized for 30 frames. Opt-in
/// (REDACTION_PROBE=1): it sets up an Avalonia application, which a process can do only once, and reads the private
/// bytes of the whole test process, so it is best run on its own.
/// </summary>
public sealed class RedactionPreviewMemoryTests
{
    private const int PictureWidth = 3840;
    private const int PictureHeight = 2160;
    private const int Redactions = 6;
    private const int ResizeFrames = 30;
    private readonly ITestOutputHelper _output;

    public RedactionPreviewMemoryTests(ITestOutputHelper output) => _output = output;

    [OptInFact("REDACTION_PROBE=1")]
    public void RedactionsOnA4KPicture_KeepNoCopyOfIt_AndAResizeRedrawsNone()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        IScheduler testScheduler = RxSchedulers.MainThreadScheduler;
        // As in the app: a preview is redrawn from a throttle on the pool, and handed to the UI thread.
        RxSchedulers.MainThreadScheduler = AvaloniaMainThreadScheduler.Instance;
        try
        {
            using var picture = AvaloniaRenderer.Create(PictureWidth, PictureHeight, static (x, y) =>
                0xFF000000u | ((uint)(x * 7 % 256) << 16) | ((uint)(y * 13 % 256) << 8) | (uint)((x + y) % 256));
            long before = PrivateMegabytes();

            // The pin's content at half the picture's size, as on a screen at 200%.
            var content = new Size(PictureWidth / 2, PictureHeight / 2);
            var panel = new Panel { Width = content.Width, Height = content.Height };
            var controls = new List<AnnotationControl>();
            for (int i = 0; i < Redactions; i++)
            {
                var start = new Point(80 + (i * 300), 120 + (i * 80));
                var annotation = new Annotation
                {
                    Type = i % 2 == 0 ? AnnotationType.Mosaic : AnnotationType.Blur,
                    StartPoint = start,
                    EndPoint = start + new Point(240, 160),
                    DrawingModeSnapshot = picture,
                    DrawingModeReferenceSize = content,
                    EffectSettings = new AnnotationEffectSettings { MosaicCellSize = 15, BlurRadius = 12f },
                };
                var control = new AnnotationControl
                {
                    DataContext = annotation,
                    Annotation = annotation,
                    Width = content.Width,
                    Height = content.Height,
                };
                controls.Add(control);
                panel.Children.Add(control);
            }

            var window = new Window { Content = panel, Width = content.Width, Height = content.Height };
            window.Show();
            Settle();
            int previews = controls.Count(c => c.EffectPreviewBitmap != null);
            long shown = PrivateMegabytes();

            // A pin being resized: each control takes the pin's size, one frame after another.
            int redraws = 0;
            var counting = controls
                .Select(c => c.GetObservable(AnnotationControl.EffectPreviewBitmapProperty).Skip(1).Subscribe(_ => redraws++))
                .ToList();
            var resizing = Stopwatch.StartNew();
            for (int frame = 1; frame <= ResizeFrames; frame++)
            {
                foreach (var control in controls)
                {
                    control.Width = content.Width - (frame * 8);
                    control.Height = content.Height - (frame * 4.5);
                }

                Pump(TimeSpan.FromMilliseconds(20));
            }

            Settle();
            resizing.Stop();
            counting.ForEach(subscription => subscription.Dispose());
            long resized = PrivateMegabytes();

            // A picture disposed while its annotation is still shown, as a pin's oldest image leaves its undo history:
            // the preview drawn from it stays when the annotation is dragged.
            var dragged = (Annotation)controls[0].DataContext!;
            var gone = AvaloniaRenderer.Create(PictureWidth, PictureHeight, static (_, _) => 0xFF808080u);
            dragged.DrawingModeSnapshot = gone;
            Settle();
            var drawnFromGone = controls[0].EffectPreviewBitmap;
            gone.Dispose();
            dragged.StartPoint += new Point(30, 20);
            dragged.EndPoint += new Point(30, 20);
            Settle();
            bool keptPreview = drawnFromGone != null && ReferenceEquals(drawnFromGone, controls[0].EffectPreviewBitmap);

            window.Close();
            Settle();
            long closed = PrivateMegabytes();

            _output.WriteLine(
                $"{previews} previews of {Redactions}; private bytes {before} MB with the picture, " +
                $"+{shown - before} MB shown, +{resized - before} MB after the resize, +{closed - before} MB closed; " +
                $"{redraws} redraws in {ResizeFrames} resize frames ({resizing.ElapsedMilliseconds} ms); " +
                $"preview kept over a disposed picture: {keptPreview}");
            Assert.Equal(Redactions, previews);
            Assert.True(keptPreview);
            // Each used to keep a copy of the whole picture, 32 MB at 4K.
            Assert.InRange(shown - before, long.MinValue, 40);
            // The previews do not depend on the control's size, so a resize redraws none of them.
            Assert.Equal(0, redraws);
        }
        finally
        {
            RxSchedulers.MainThreadScheduler = testScheduler;
        }
    }

    private static void Settle() => Pump(TimeSpan.FromMilliseconds(300));

    private static void Pump(TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        while (watch.Elapsed < duration);

        Dispatcher.UIThread.RunJobs();
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
