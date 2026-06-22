using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsHistoryTab : UserControl
{
    public SettingsHistoryTab()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Activating loads the list now and keeps it auto-refreshing while the tab is shown.
        (DataContext as MainWindowViewModel)?.SetHistoryViewActive(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as MainWindowViewModel)?.SetHistoryViewActive(false);
    }
}
