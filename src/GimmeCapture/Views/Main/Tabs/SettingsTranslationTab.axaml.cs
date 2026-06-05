using Avalonia.Controls;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsTranslationTab : UserControl
{
    public SettingsTranslationTab()
    {
        InitializeComponent();
    }

    private void OnLlamaModelPickerButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.IsLlamaModelPickerOpen = !vm.IsLlamaModelPickerOpen;
        e.Handled = true;
    }

    private void OnLlamaModelOptionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (sender is not Button button || button.Tag is not string modelId || string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        vm.LlamaModelId = modelId;
        vm.IsLlamaModelPickerOpen = false;
        e.Handled = true;
    }
}
