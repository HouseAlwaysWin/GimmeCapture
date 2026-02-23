using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Main;
using System;
using System.Linq;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsHotkeysTab : UserControl
{
    private bool _tagsValidated;

    public SettingsHotkeysTab()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_tagsValidated) return;
        _tagsValidated = true;

        if (DataContext is not MainWindowViewModel vm) return;

        var tags = this.GetVisualDescendants()
            .OfType<TextBox>()
            .Select(tb => tb.Tag as string)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Cast<string>()
            .ToList();

        var unknown = vm.HotkeyMappingService.ValidateTags(tags);
        if (unknown.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsHotkeysTab] Unknown hotkey tags: {string.Join(", ", unknown)}");
        }
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
