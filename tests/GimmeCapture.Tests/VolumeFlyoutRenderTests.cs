using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace GimmeCapture.Tests;

// Offscreen "snapshot" render of the vertical volume popup so its centering can be verified without a
// device/display. Gated behind RENDER_PROBE=1 (headless Skia render) so the normal CI test run stays
// render-free; run manually with RENDER_PROBE=1 to (re)generate the PNG and assert the track is centred.
public class VolumeFlyoutRenderTests
{
    private static readonly string OutDir =
        Environment.GetEnvironmentVariable("RENDER_OUT") ?? Path.Combine(Path.GetTempPath(), "gimmecapture-render");

    // Mirrors the production popup content in VolumeFlyoutButton.axaml (native vertical slider + inline
    // track-fix style). The track-fix style is inline on the Slider on purpose: a Popup is a separate
    // visual tree that UserControl/App styles don't reach.
    private static Border BuildPopupContent(double volume)
    {
        var slider = new Slider
        {
            Orientation = Orientation.Vertical,
            Height = 120,
            Width = 30,
            Minimum = 0,
            Maximum = 1,
            Value = volume,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var trackFix = new Style(x => x.OfType<Slider>().Template().OfType<Border>().Name("TrackBackground"));
        trackFix.Setters.Add(new Setter(Layoutable.WidthProperty, 3.0));
        trackFix.Setters.Add(new Setter(Layoutable.HeightProperty, double.NaN));
        trackFix.Setters.Add(new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        trackFix.Setters.Add(new Setter(Layoutable.VerticalAlignmentProperty, VerticalAlignment.Stretch));
        slider.Styles.Add(trackFix);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = volume.ToString("0%"), FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(slider);
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 10),
            Width = 120,
            Child = stack,
        };
    }

    [Fact]
    public void VolumePopup_TrackIsHorizontallyCentered()
    {
        if (Environment.GetEnvironmentVariable("RENDER_PROBE") != "1")
        {
            return; // gated: headless Skia render, run manually with RENDER_PROBE=1
        }

        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        Directory.CreateDirectory(OutDir);

        const int W = 160, H = 220;
        Border content = BuildPopupContent(0.5);
        var window = new Window { Content = content, Width = W, Height = H, Background = new SolidColorBrush(Color.Parse("#101010")) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rtb = new RenderTargetBitmap(new PixelSize(W, H), new Vector(96, 96));
        rtb.Render(window);
        rtb.Save(Path.Combine(OutDir, "volume_popup_production.png"));

        // Find the accent-coloured (red) thumb/track pixels and assert their horizontal centre ≈ image centre.
        int minX = int.MaxValue, maxX = int.MinValue;
        var buffer = new byte[W * H * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, W, H), handle.AddrOfPinnedObject(), buffer.Length, W * 4);
        }
        finally
        {
            handle.Free();
        }
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                byte b = buffer[i], g = buffer[i + 1], r = buffer[i + 2];
                if (r > 150 && g < 90 && b < 90) // accent red thumb/fill
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }
        }

        Assert.True(maxX >= minX, "No accent-coloured slider pixels found in the render.");
        double centreX = (minX + maxX) / 2.0;
        Assert.InRange(centreX, W / 2.0 - 6, W / 2.0 + 6); // within 6px of horizontal centre
    }
}
