using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Dialogs;
using System;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsHotkeysTab : UserControl
{
    private bool _tagsValidated;
    private bool _isSuspended;

    public SettingsHotkeysTab()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_tagsValidated) return;

        if (DataContext is not MainWindowViewModel vm) return;
        _tagsValidated = true;

        var tags = this.GetVisualDescendants()
            .AsValueEnumerable()
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

    private void EnsureSuspended()
    {
        if (_isSuspended) return;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.HotkeyService.SuspendAll();
            _isSuspended = true;
        }
    }

    private void EnsureResumed()
    {
        if (!_isSuspended) return;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.HotkeyService.ResumeAll();
            _isSuspended = false;
        }
    }

    private void HotkeyTextBox_GotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        EnsureSuspended();
    }

    private void HotkeyTextBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        EnsureResumed();
    }

    private async void HotkeyTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        EnsureSuspended();

        var key = e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            EnsureResumed();
            if (sender is TextBox tb)
                (tb.GetVisualParent() as Control)?.Focus();
            e.Handled = true;
            return;
        }

        var modifiers = e.KeyModifiers;
        var hotkeyStr = "";

        if (modifiers.HasFlag(KeyModifiers.Control)) hotkeyStr += "Ctrl+";
        if (modifiers.HasFlag(KeyModifiers.Alt)) hotkeyStr += "Alt+";
        if (modifiers.HasFlag(KeyModifiers.Shift)) hotkeyStr += "Shift+";

        hotkeyStr += key.ToString();

        if (DataContext is MainWindowViewModel vm && sender is TextBox textBox && textBox.Tag is string tag)
        {
            var conflict = vm.CheckHotkeyConflict(tag, hotkeyStr);
            if (conflict != null)
            {
                var conflictName = GimmeCapture.Services.Core.Infrastructure.LocalizationService.Instance[conflict] ?? conflict;
                vm.StatusText = $"[Conflict] {hotkeyStr} -> {conflictName}";
                System.Diagnostics.Debug.WriteLine($"[HotkeyConflict] {hotkeyStr} conflicts with {conflict} for tag {tag}");

                if (TopLevel.GetTopLevel(this) is Window owner)
                {
                    await ConfirmationDialog.ShowConfirmation(
                        owner,
                        "\u5feb\u6377\u9375\u885d\u7a81",
                        $"\u5feb\u6377\u9375 {hotkeyStr} \u5df2\u88ab\u300c{conflictName}\u300d\u4f7f\u7528\u3002\u8acb\u6539\u7528\u5176\u4ed6\u7d44\u5408\u3002",
                        ConfirmationMode.OkOnly);

                    if (textBox.IsFocused)
                    {
                        EnsureSuspended();
                    }
                }
            }
            else
            {
                vm.HotkeyMappingService.UpdateViewModelHotkey(vm, tag, hotkeyStr);
            }
        }

        e.Handled = true;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        EnsureResumed();
    }
}
