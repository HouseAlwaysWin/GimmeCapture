using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main;

// A brief floating notification. Topmost, non-activating and excluded from screen capture
// (SetWindowCaptureVisibility false) so it never appears in a screenshot/recording and never
// steals focus. Auto-dismisses after a short delay; positioning/stacking is owned by the
// AvaloniaToastService. Mirrors the ScrollingCaptureHintWindow transient-window pattern.
public sealed class ToastWindow : Window
{
    private const int DismissMs = 2600;

    /// <summary>Logical size of the square the preview thumbnail letterboxes into.</summary>
    private const int PreviewBox = 56;

    private readonly DispatcherTimer _dismissTimer;

    /// <summary>Raised once the window is open and has a measured size (ready to be positioned).</summary>
    public event EventHandler? ReadyForLayout;

    /// <param name="preview">
    /// Optional thumbnail rendered left of the message. Owned by this window and disposed on close — the caller
    /// creates one detached thumbnail per toast rather than handing over a bitmap it still uses.
    /// </param>
    public ToastWindow(string message, MainWindowViewModel.ToastSeverity severity, Bitmap? preview = null)
    {
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false; // never steal focus from the foreground app
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        MaxWidth = 360;

        var accent = severity switch
        {
            MainWindowViewModel.ToastSeverity.Success => Color.Parse("#2ECC71"),
            MainWindowViewModel.ToastSeverity.Error => Color.Parse("#E60012"),
            _ => Color.Parse("#9A9A9A"),
        };

        // The thumbnail and its margin come out of the text's share of the toast's MaxWidth; leaving the text at
        // its full width would push the content past the window cap and clip it.
        double textMaxWidth = preview != null ? 320 - PreviewBox - 10 : 320;

        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = textMaxWidth,
            VerticalAlignment = VerticalAlignment.Center
        };

        var accentBar = new Border
        {
            Width = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(accent),
            Margin = new Thickness(0, 0, 10, 0)
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(accentBar);

        if (preview != null)
        {
            // Uniform inside a fixed box: a wide screenshot and a tall one both letterbox into the same slot, so
            // the message never shifts around depending on what was captured.
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Image
                {
                    Source = preview,
                    Stretch = Stretch.Uniform,
                    Width = PreviewBox,
                    Height = PreviewBox
                }
            });
        }

        panel.Children.Add(text);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10),
            Child = panel
        };

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DismissMs) };
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            Close();
        };

        Opened += (_, _) =>
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            _dismissTimer.Start();
            ReadyForLayout?.Invoke(this, EventArgs.Empty);
        };

        Closed += (_, _) =>
        {
            _dismissTimer.Stop();
            preview?.Dispose();
        };
    }
}
