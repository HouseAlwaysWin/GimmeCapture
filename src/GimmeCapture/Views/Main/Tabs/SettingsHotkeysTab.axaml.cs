using Avalonia.Controls;
using Avalonia.Input;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsHotkeysTab : UserControl
{
    public SettingsHotkeysTab()
    {
        InitializeComponent();
    }

    private void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Don't Record purely modifier keys
        var key = e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        var hotkeyStr = "";

        if (modifiers.HasFlag(KeyModifiers.Control)) hotkeyStr += "Ctrl+";
        if (modifiers.HasFlag(KeyModifiers.Alt)) hotkeyStr += "Alt+";
        if (modifiers.HasFlag(KeyModifiers.Shift)) hotkeyStr += "Shift+";

        hotkeyStr += key.ToString();

        if (DataContext is MainWindowViewModel vm && sender is TextBox tb && tb.Tag is string tag)
        {
            // Use service to update ViewModel hotkey
            vm.HotkeyMappingService.UpdateViewModelHotkey(vm, tag, hotkeyStr);
        }

        e.Handled = true;
    }
}
