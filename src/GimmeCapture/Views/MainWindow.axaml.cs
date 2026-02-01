using Avalonia.Controls;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using GimmeCapture.ViewModels;

namespace GimmeCapture.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        this.PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Minimize to Tray
                Hide();
            }
        }
    }

    private void HotkeyTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Don't Record purely modifier keys
        var key = e.Key;
        if (key == Avalonia.Input.Key.LeftCtrl || key == Avalonia.Input.Key.RightCtrl ||
            key == Avalonia.Input.Key.LeftAlt || key == Avalonia.Input.Key.RightAlt ||
            key == Avalonia.Input.Key.LeftShift || key == Avalonia.Input.Key.RightShift ||
            key == Avalonia.Input.Key.LWin || key == Avalonia.Input.Key.RWin)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        var hotkeyStr = "";

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) hotkeyStr += "Ctrl+";
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) hotkeyStr += "Alt+";
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) hotkeyStr += "Shift+";

        hotkeyStr += key.ToString();

        if (DataContext is GimmeCapture.ViewModels.MainWindowViewModel vm && sender is TextBox tb)
        {
            if (tb.Name == "SnipHotkeyBox") vm.SnipHotkey = hotkeyStr;
            else if (tb.Name == "CopyHotkeyBox") vm.CopyHotkey = hotkeyStr;
            else if (tb.Name == "PinHotkeyBox") vm.PinHotkey = hotkeyStr;
        }

        e.Handled = true;
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is GimmeCapture.ViewModels.MainWindowViewModel vm)
        {
            // Initialize Hotkey Service with this Window
            vm.HotkeyService.Initialize(this);

            vm.RequestCaptureAction = (mode) =>
            {
                var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                var existing = desktop?.Windows.OfType<SnipWindow>().FirstOrDefault();

                if (existing != null)
                {
                    if (existing.DataContext is SnipWindowViewModel existingVm)
                    {
                        existingVm.AutoActionMode = (int)mode;
                    }
                    existing.Activate();
                    return;
                }

                var snip = new SnipWindow();
                var snipVm = new SnipWindowViewModel(
                    vm.BorderColor, 
                    vm.BorderThickness, 
                    vm.MaskOpacity
                );
                snipVm.AutoActionMode = (int)mode;
                snip.DataContext = snipVm;
                snip.Show();
            };
        }
    }
}