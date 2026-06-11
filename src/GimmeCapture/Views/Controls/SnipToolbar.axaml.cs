using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

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
