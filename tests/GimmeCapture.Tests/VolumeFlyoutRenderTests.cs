using System;
using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using GimmeCapture.Views.Controls;
using Xunit;

namespace GimmeCapture.Tests;

// Offscreen "snapshot" render that MEASURES the horizontal centering of the vertical volume slider.
// Gated behind RENDER_PROBE=1. Writes PNGs + a measurements file so the exact accent-pixel offset from
// centre can be inspected.
public class VolumeFlyoutRenderTests
{
    private static readonly string OutDir =
        Environment.GetEnvironmentVariable("RENDER_OUT") ?? Path.Combine(Path.GetTempPath(), "gimmecapture-render");

    private static Slider MakeVerticalSlider(double? width, bool trackFix)
    {
        var slider = new Slider { Orientation = Orientation.Vertical, Height = 120, Minimum = 0, Maximum = 1, Value = 0.5, HorizontalAlignment = HorizontalAlignment.Center };
        if (width.HasValue)
        {
            slider.Width = width.Value;
        }
        if (trackFix)
        {
            var s = new Style(x => x.OfType<Slider>().Template().OfType<Border>().Name("TrackBackground"));
            s.Setters.Add(new Setter(Layoutable.WidthProperty, 3.0));
            s.Setters.Add(new Setter(Layoutable.HeightProperty, double.NaN));
            s.Setters.Add(new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Center));
            s.Setters.Add(new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Stretch));
            slider.Styles.Add(s);
        }
        return slider;
    }

    private static Border WrapPopup(Control slider)
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = "50%", FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(slider);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 10),
            Child = stack,
        };
    }

    // Returns (accentCentreX, imageCentreX) after rendering `content` centred in a WxH window.
    private static (double accent, double centre) Measure(Control content, string pngName, int w = 120, int h = 200)
    {
        var window = new Window { Content = content, Width = w, Height = h, Background = new SolidColorBrush(Color.Parse("#101010")) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        rtb.Render(window);
        rtb.Save(Path.Combine(OutDir, pngName));
        double accentWin = AccentCentreX(rtb, w, h);
        window.Close();
        Dispatcher.UIThread.RunJobs();
        return (accentWin, w / 2.0);
    }

    // Accent-pixel horizontal centre of an already-rendered bitmap.
    private static double AccentCentreX(RenderTargetBitmap rtb, int w, int h)
    {
        var buffer = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buffer.Length, w * 4);
        }
        finally
        {
            handle.Free();
        }

        int minX = int.MaxValue, maxX = int.MinValue;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
                if (r > 150 && g < 90 && b < 90)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }
        }

        return (minX + maxX) / 2.0;
    }

    [Fact]
    public void Measure_VolumeSliderCentering()
    {
        if (Environment.GetEnvironmentVariable("RENDER_PROBE") != "1")
        {
            return;
        }

        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Directory.CreateDirectory(OutDir);
        var sb = new StringBuilder();

        foreach (double? width in new double?[] { 16, 18, 20, 24, 30, null })
        {
            string tag = width.HasValue ? ((int)width.Value).ToString() : "auto";
            (double accent, double centre) = Measure(WrapPopup(MakeVerticalSlider(width, trackFix: true)), $"vol_w{tag}.png");
            sb.AppendLine($"native width={tag}: accentCentre={accent:0.0}  imageCentre={centre:0.0}  offset={accent - centre:+0.0;-0.0}");
        }

        // The real production control:
        var vfb = new VolumeFlyoutButton { Volume = 0.5 };
        var host = new Window { Content = vfb, Width = 200, Height = 260, Background = new SolidColorBrush(Color.Parse("#101010")) };
        host.Show();
        Dispatcher.UIThread.RunJobs();
        Popup? popup = vfb.GetLogicalDescendants().OfType<Popup>().FirstOrDefault();
        if (popup?.Child is Control child)
        {
            popup.IsOpen = true;
            Dispatcher.UIThread.RunJobs();
            int cw = Math.Max(1, (int)Math.Ceiling(child.Bounds.Width));
            int ch = Math.Max(1, (int)Math.Ceiling(child.Bounds.Height));
            var rtb = new RenderTargetBitmap(new PixelSize(cw, ch), new Vector(96, 96));
            rtb.Render(child); // render in place (it's already parented to the open popup host)
            rtb.Save(Path.Combine(OutDir, "vol_actual_control.png"));
            double accent = AccentCentreX(rtb, cw, ch);
            double offset = accent - cw / 2.0;
            sb.AppendLine($"ACTUAL control: accentCentre={accent:0.0}  imageWidth={cw}  imageCentre={cw / 2.0:0.0}  offset={offset:+0.0;-0.0}");
            File.WriteAllText(Path.Combine(OutDir, "centering_measurements.txt"), sb.ToString());

            // Regression guard: the real VolumeFlyoutButton slider must be horizontally centred (±2px).
            Assert.InRange(offset, -2.0, 2.0);
            return;
        }

        File.WriteAllText(Path.Combine(OutDir, "centering_measurements.txt"), sb.ToString());
    }
}
