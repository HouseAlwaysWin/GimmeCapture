using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
    private readonly DispatcherTimer _dismissTimer;

    /// <summary>Raised once the window is open and has a measured size (ready to be positioned).</summary>
    public event EventHandler? ReadyForLayout;

    public ToastWindow(string message, MainWindowViewModel.ToastSeverity severity)
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

        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
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

        Closed += (_, _) => _dismissTimer.Stop();
    }
}
