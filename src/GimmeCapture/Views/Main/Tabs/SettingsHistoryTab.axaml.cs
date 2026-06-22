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
        // Refresh the list when the tab first appears (captures may have been added while in tray).
        if (DataContext is MainWindowViewModel vm)
        {
            vm.LoadHistoryAsync().Forget("History.LoadOnAttach");
        }
    }
}
