using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Controls;

public partial class SnipToolbar : UserControl
{
    public SnipToolbar()
    {
        InitializeComponent();

        var translateBtn = this.FindControl<Button>("TranslateAllButton");
        if (translateBtn != null)
        {
            translateBtn.Click += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine("[SnipToolbar] TranslateAllButton physically CLICKED");
            };
        }

        // Rebuild the monitor/window list each time the capture-scope picker button is pressed (which
        // opens its flyout) so the list reflects the windows currently on screen.
        var captureScopeBtn = this.FindControl<Button>("CaptureScopeButton");
        if (captureScopeBtn != null)
        {
            captureScopeBtn.Click += (_, _) =>
                (DataContext as SnipWindowViewModel)?.RefreshCaptureTargets();
        }
    }

    private void OnCaptureTargetSelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CaptureTargetItem target
            && DataContext is SnipWindowViewModel vm)
        {
            vm.SelectCaptureTargetCommand.Execute(target).Subscribe();
        }

        // Close the picker after a target is chosen (let the command run first).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<Button>("CaptureScopeButton")?.Flyout?.Hide();
        });
    }

    private void OnColorSelected(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Close the flyout when a color is selected
        // We use Dispatcher.UIThread.Post to allow the Command to execute first
        // otherwise closing the flyout might detach the visual/context too early
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.FindControl<Button>("SnipStyleButton")?.Flyout?.Hide();
            this.FindControl<Button>("RecordStyleButton")?.Flyout?.Hide();
        });
    }
}
