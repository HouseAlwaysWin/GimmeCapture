using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Views.Main;

public sealed class CaptureCountdownWindow : Window
{
    private readonly TextBlock _countdownText;

    public CaptureCountdownWindow(Action cancel)
    {
        ArgumentNullException.ThrowIfNull(cancel);

        Width = 112;
        Height = 112;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _countdownText = new TextBlock
        {
            FontSize = 42,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Content = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(224, 24, 24, 24)),
            BorderBrush = new SolidColorBrush(Color.Parse("#E60012")),
            BorderThickness = new Thickness(2),
            Child = _countdownText
        };

        Opened += (_, _) =>
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
            Activate();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
            {
                return;
            }

            cancel();
            e.Handled = true;
        };
    }

    public void UpdateCountdown(int seconds)
    {
        _countdownText.Text = seconds.ToString();
    }
}
