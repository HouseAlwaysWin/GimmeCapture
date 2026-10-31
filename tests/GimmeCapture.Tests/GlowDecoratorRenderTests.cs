using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Converters;
using GimmeCapture.Views.Controls;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The glow <see cref="GlowDecorator"/> draws with Skia's mask blur, and <see cref="TransparencyCheckerboard"/>, against
/// what they replace: the BoxShadow a Border drew the glow with, and the VisualBrush of two rectangles. Rendered by
/// Avalonia's own Skia renderer at the border widths, opacities and screen scales the app meets, every channel of the
/// glow came within 3 levels at 100%, 5 at 125% and 7 at 150% (0.04-0.18 on average); the checkerboard was identical at
/// 100% and 200%, and at 125% and 150% its squares' edges are smoothed rather than antialiased, at most 26 levels off
/// (1.5-3.1 on average). Opt-in (GLOW_PROBE=1): it sets up Avalonia's headless platform, which a process can do only
/// once. GLOW_OUT names a folder for the pictures.
/// </summary>
public sealed class GlowDecoratorRenderTests
{
    private static readonly Color Red = Color.Parse("#E60012");
    private static readonly Color Backdrop = Color.Parse("#3060A0");

    private readonly ITestOutputHelper _output;

    public GlowDecoratorRenderTests(ITestOutputHelper output) => _output = output;

    [OptInFact("GLOW_PROBE=1")]
    public void TheGlowAndTheCheckerboard_LookAsBefore()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        string? outDir = Environment.GetEnvironmentVariable("GLOW_OUT");
        if (outDir != null)
        {
            Directory.CreateDirectory(outDir);
        }

        var glow = (BoxShadows)new ColorToBoxShadowConverter().Convert(Red, typeof(BoxShadows), null, CultureInfo.InvariantCulture)!;
        foreach (double scale in new[] { 1.0, 1.25, 1.5 })
        {
            foreach (double thickness in new[] { 0.0, 2.0 })
            {
                foreach (double opacity in new[] { 1.0, 0.5 })
                {
                    string name = $"glow_{scale:0.00}x_t{thickness:0}_o{opacity:0.0}";
                    var before = Render(Framed(glow, thickness, opacity, decorator: false), scale, outDir, name + "_before");
                    var after = Render(Framed(glow, thickness, opacity, decorator: true), scale, outDir, name + "_after");
                    var (max, mean, off) = Compare(before, after);
                    _output.WriteLine($"{name}: largest difference {max} levels, {mean:0.000} on average, {off} channels off by more than 2");
                    Assert.True(max <= 8 && mean <= 0.25, $"{name}: {max} levels at most, {mean:0.000} on average.");
                }
            }
        }

        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            string name = $"checker_{scale:0.00}x";
            var before = Render(new Border { Width = 203, Height = 121, Background = OldCheckerboard() }, scale, outDir, name + "_before");
            var after = Render(new TransparencyCheckerboard { Width = 203, Height = 121 }, scale, outDir, name + "_after");
            var (max, mean, off) = Compare(before, after);
            _output.WriteLine($"{name}: largest difference {max} levels, {mean:0.000} on average, {off} channels off by more than 2");
            bool wholeScale = scale % 1 == 0;
            Assert.True(
                wholeScale ? max == 0 : max <= 30 && mean <= 4,
                $"{name}: {max} levels at most, {mean:0.000} on average.");
        }
    }

    // A frame like a pin's: the border colour's stroke around a dark fill, on a coloured backdrop for the glow to show on.
    private static Control Framed(BoxShadows glow, double thickness, double opacity, bool decorator)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(thickness),
            BorderBrush = new SolidColorBrush(Red),
            Background = new SolidColorBrush(Color.Parse("#141414")),
        };
        Control frame;
        if (decorator)
        {
            frame = new GlowDecorator { Glow = glow, Child = border };
        }
        else
        {
            border.BoxShadow = glow;
            frame = border;
        }

        frame.Width = 237;
        frame.Height = 141;
        frame.Opacity = opacity;
        frame.HorizontalAlignment = HorizontalAlignment.Center;
        frame.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Background = new SolidColorBrush(Backdrop), Width = 320, Height = 220, Child = frame };
    }

    // The VisualBrush the pins and the capture overlay used before TransparencyCheckerboard.
    private static IBrush OldCheckerboard()
    {
        var tile = new Canvas { Width = 10, Height = 10, Background = Brushes.White };
        tile.Children.Add(new Rectangle { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.Parse("#CCCCCC")) });
        var second = new Rectangle { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.Parse("#CCCCCC")) };
        Canvas.SetLeft(second, 5);
        Canvas.SetTop(second, 5);
        tile.Children.Add(second);
        return new VisualBrush(tile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            SourceRect = new RelativeRect(0, 0, 10, 10, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, 10, 10, RelativeUnit.Absolute),
        };
    }

    private static byte[] Render(Control content, double scale, string? outDir, string name)
    {
        var window = new Window { Content = content, SizeToContent = SizeToContent.WidthAndHeight };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var size = new PixelSize((int)Math.Ceiling(content.Bounds.Width * scale), (int)Math.Ceiling(content.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(content);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        if (outDir != null)
        {
            bitmap.Save(System.IO.Path.Combine(outDir, name + ".png"), PngBitmapEncoderOptions.Default);
        }

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

    private static (int Max, double Mean, int Off) Compare(byte[] before, byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        int max = 0, off = 0;
        long sum = 0;
        for (int i = 0; i < before.Length; i++)
        {
            int difference = Math.Abs(before[i] - after[i]);
            max = Math.Max(max, difference);
            sum += difference;
            off += difference > 2 ? 1 : 0;
        }

        return (max, (double)sum / before.Length, off);
    }
}
