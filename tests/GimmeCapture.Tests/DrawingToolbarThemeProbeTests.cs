using System;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GimmeCapture.Views.Controls;
using Xunit;

namespace GimmeCapture.Tests;

// Diagnostic probe (RENDER_PROBE=1): renders a DrawingToolbar with ButtonTheme and reports what the
// re-theme walk actually finds, so theme-application bugs are observed rather than guessed at.
public class DrawingToolbarThemeProbeTests
{
    private static readonly string OutDir =
        Environment.GetEnvironmentVariable("RENDER_OUT") ?? Path.Combine(Path.GetTempPath(), "gimmecapture-render");

    [Fact]
    public void Probe_DrawingToolbarButtonTheme()
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

        object? themeObj = Application.Current!.TryFindResource("GothicToolMetalButton", out object? t) ? t : null;
        sb.AppendLine($"GothicToolMetalButton resource: {(themeObj is ControlTheme ? "FOUND" : $"MISSING ({themeObj?.GetType().Name ?? "null"})")}");

        var toolbar = new DrawingToolbar { ShowHistory = false };
        if (themeObj is ControlTheme theme)
        {
            toolbar.ButtonTheme = theme;
        }

        // Reproduce the editor's failure mode: the sub-toolbar starts HIDDEN (no category picked) and is
        // only made visible later — the re-theme must survive that (visual tree not materialised at load).
        var host = new Border { Child = toolbar, IsVisible = false };
        var window = new Window { Content = host, Width = 460, Height = 90, Background = new SolidColorBrush(Color.Parse("#101010")) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        host.IsVisible = true; // user clicks 標註
        Dispatcher.UIThread.RunJobs();

        var buttons = toolbar.GetVisualDescendants().OfType<Button>().ToList();
        sb.AppendLine($"visual-descendant Buttons found: {buttons.Count}");
        foreach (Button b in buttons)
        {
            string themeName =
                b.Theme == themeObj ? "GOLD(applied)" :
                b.Theme == (Application.Current!.TryFindResource("SnipToolButton", out object? st) ? st : null) ? "SnipToolButton" :
                b.Theme == (Application.Current!.TryFindResource("SnipButton", out object? sn) ? sn : null) ? "SnipButton" :
                b.Theme?.ToString() ?? "null";
            sb.AppendLine($"  {b.Name ?? b.GetType().Name}: Theme={themeName}  IsVisible={b.IsVisible}");
        }

        var rtb = new RenderTargetBitmap(new PixelSize(460, 90), new Vector(96, 96));
        rtb.Render(window);
        rtb.Save(Path.Combine(OutDir, "drawingtoolbar_probe.png"));

        File.WriteAllText(Path.Combine(OutDir, "drawingtoolbar_probe.txt"), sb.ToString());
    }
}
