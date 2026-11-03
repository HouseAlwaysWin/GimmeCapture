using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using GimmeCapture.Converters;
using GimmeCapture.Views.Controls;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="CandidateBoxesLayer"/> against what it replaces: the snip overlay's ItemsControl of Borders, as
/// SnipWindow.axaml built it (a Canvas of content presenters, each Border's brush bound through ColorToBrushConverter,
/// its opacity a layer, the text boxes' pointer-over style). Rendered by Avalonia's own Skia renderer, the three layers'
/// strokes, on boxes at fractional positions, must come out the same; and filling the overlay with a scan's 100 text
/// boxes and 20 windows is timed to its first frame, both ways. Opt-in (CANDIDATE_BOXES_PROBE=1): it sets up Avalonia's
/// headless platform, which a process can do only once.
/// </summary>
public sealed class CandidateBoxesLayerRenderTests(ITestOutputHelper output)
{
    private static readonly Color Red = Color.Parse("#E60012");

    public sealed class Box(double x, double y, double width, double height)
    {
        public double X { get; } = x;
        public double Y { get; } = y;
        public double Width { get; } = width;
        public double Height { get; } = height;
    }

    public sealed class OverlayContext
    {
        public Color OcrHighlightColor => Red;
    }

    [OptInFact("CANDIDATE_BOXES_PROBE=1")]
    public void TheBoxesLookAsBefore_AndCostAFractionOfTheTime()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Rect[] boxes =
        [
            new(10.4, 20.6, 30.2, 10.1), new(50, 40, 80, 14), new(12.5, 70.5, 100.5, 20.5), new(140.7, 8.3, 41.6, 61.9),
            new(60.2, 100.2, 19.6, 7.4), new(30, 30, 60, 50), new(1, 1, 1.5, 1.5), new(150, 90, 40.2, 40.8),
        ];
        foreach (var (thickness, opacity, hoverStyle) in new[] { (1.0, 0.4, true), (1.0, 0.24, false), (2.0, 0.55, false) })
        {
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                byte[] before = Render(OldLayer(new(boxes.Select(b => new Box(b.X, b.Y, b.Width, b.Height))), thickness, opacity, hoverStyle), scale);
                byte[] after = Render(NewLayer(boxes, thickness, opacity), scale);
                var (max, mean) = Compare(before, after);
                int stroked = Stroked(after, scale);
                output.WriteLine(
                    $"thickness {thickness}, opacity {opacity}, {scale:0.00}x: {stroked} pixels stroked; largest difference " +
                    $"{max} levels, {mean:0.0000} on average");
                Assert.True(stroked > 400 * scale * scale * thickness, $"Only {stroked} pixels were stroked."); // not two blanks
                Assert.True(max <= 2, $"thickness {thickness}, opacity {opacity}, {scale:0.00}x: {max} levels at most.");
            }
        }

        // A scan's boxes coming in, to the frame that shows them: as before, then as now, then both again.
        var random = new Random(5);
        foreach (bool old in new[] { true, false, true, false })
        {
            var times = new List<(double Ui, double Frame)>();
            for (int round = 0; round < 5; round++)
            {
                Rect[] text = Enumerable.Range(0, 100)
                    .Select(_ => new Rect(random.Next(0, 1800), random.Next(0, 1000), random.Next(20, 400), random.Next(12, 30))).ToArray();
                Rect[] windows = Enumerable.Range(0, 20)
                    .Select(_ => new Rect(random.Next(0, 1200), random.Next(0, 600), random.Next(300, 900), random.Next(200, 600))).ToArray();
                times.Add(FillToFirstFrame(old, text, windows));
            }

            output.WriteLine(
                $"{(old ? "ItemsControl of Borders" : "CandidateBoxesLayer    ")}: UI thread " +
                $"{string.Join(", ", times.Select(t => t.Ui.ToString("F1")))} ms, then the frame " +
                $"{string.Join(", ", times.Select(t => t.Frame.ToString("F1")))} ms");
        }
    }

    // A new overlay-sized window, its two candidate layers filled with a scan's boxes: the UI thread's part, the boxes
    // handed over and laid out, then the frame that shows them, which the app's compositor draws on its render thread.
    private static (double Ui, double Frame) FillToFirstFrame(bool old, Rect[] text, Rect[] windows)
    {
        var textBoxes = new ObservableCollection<Box>();
        var windowBoxes = new ObservableCollection<Box>();
        var textLayer = new CandidateBoxesLayer { BoxColor = Red, BoxOpacity = 0.4, Width = 1920, Height = 1080 };
        var windowLayer = new CandidateBoxesLayer { BoxColor = Red, BoxOpacity = 0.24, Width = 1920, Height = 1080 };
        var grid = new Grid { Background = Brushes.Black };
        grid.Children.Add(old ? OldLayer(textBoxes, 1, 0.4, hoverStyle: true) : textLayer);
        grid.Children.Add(old ? OldLayer(windowBoxes, 1, 0.24, hoverStyle: false) : windowLayer);
        var window = new Window { Width = 1920, Height = 1080, DataContext = new OverlayContext(), Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var watch = Stopwatch.StartNew();
        if (old)
        {
            foreach (Rect box in text)
            {
                textBoxes.Add(new Box(box.X, box.Y, box.Width, box.Height));
            }

            foreach (Rect box in windows)
            {
                windowBoxes.Add(new Box(box.X, box.Y, box.Width, box.Height));
            }
        }
        else
        {
            textLayer.Boxes = text;
            windowLayer.Boxes = windows;
        }

        Dispatcher.UIThread.RunJobs();
        double ui = watch.Elapsed.TotalMilliseconds;
        window.CaptureRenderedFrame();
        double frame = watch.Elapsed.TotalMilliseconds - ui;
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return (ui, frame);
    }

    private static CandidateBoxesLayer NewLayer(Rect[] boxes, double thickness, double opacity) =>
        new() { Boxes = boxes, BoxColor = Red, BoxThickness = thickness, BoxOpacity = opacity, Width = 200, Height = 140 };

    // SnipWindow.axaml's candidate layer before CandidateBoxesLayer, in code.
    private static ItemsControl OldLayer(ObservableCollection<Box> items, double thickness, double opacity, bool hoverStyle)
    {
        var control = new ItemsControl
        {
            ItemsSource = items,
            IsHitTestVisible = false,
            Width = items.Count == 0 ? 1920 : 200,
            Height = items.Count == 0 ? 1080 : 140,
            ItemsPanel = new FuncTemplate<Panel?>(() => new Canvas()),
        };
        var position = new Style(x => x.OfType<ItemsControl>().Child().OfType<ContentPresenter>());
        position.Setters.Add(new Setter(Canvas.LeftProperty, new Binding(nameof(Box.X))));
        position.Setters.Add(new Setter(Canvas.TopProperty, new Binding(nameof(Box.Y))));
        control.Styles.Add(position);
        control.ItemTemplate = new FuncDataTemplate<Box>((_, _) =>
        {
            var border = new Border
            {
                BorderThickness = new Thickness(thickness),
                Background = Brushes.Transparent,
                Opacity = opacity,
            };
            border.Bind(Border.BorderBrushProperty, HighlightBrush());
            border.Bind(Layoutable.WidthProperty, new Binding(nameof(Box.Width)));
            border.Bind(Layoutable.HeightProperty, new Binding(nameof(Box.Height)));
            if (hoverStyle)
            {
                var style = new Style(x => x.OfType<Border>().Class(":pointerover"));
                style.Setters.Add(new Setter(Border.BorderBrushProperty, HighlightBrush()));
                style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(3)));
                style.Setters.Add(new Setter(Border.BackgroundProperty, HighlightBrush()));
                style.Setters.Add(new Setter(Visual.OpacityProperty, 0.2));
                border.Styles.Add(style);
            }

            return border;
        });
        return control;
    }

    private static Binding HighlightBrush() => new("DataContext.OcrHighlightColor")
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Window) },
        Converter = new ColorToBrushConverter(),
    };

    // The layer on a dark backdrop, rendered at scale, as BGRA.
    private static byte[] Render(Control layer, double scale)
    {
        var content = new Border { Background = new SolidColorBrush(Color.Parse("#202830")), Width = 200, Height = 140, Child = layer };
        var window = new Window
        {
            Content = content,
            SizeToContent = SizeToContent.WidthAndHeight,
            DataContext = new OverlayContext(),
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var size = new PixelSize((int)Math.Ceiling(200 * scale), (int)Math.Ceiling(140 * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(content);
        window.Close();
        Dispatcher.UIThread.RunJobs();

        var pixels = new byte[size.Width * size.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    // How many pixels differ from the backdrop: the strokes.
    private static int Stroked(byte[] pixels, double scale)
    {
        int stroked = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0x30 || pixels[i + 1] != 0x28 || pixels[i + 2] != 0x20)
            {
                stroked++;
            }
        }

        return stroked;
    }

    private static (int Max, double Mean) Compare(byte[] a, byte[] b)
    {
        int max = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int difference = Math.Abs(a[i] - b[i]);
            max = Math.Max(max, difference);
            sum += difference;
        }

        return (max, (double)sum / a.Length);
    }
}
