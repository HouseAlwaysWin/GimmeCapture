using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Controls;

public partial class SnipToolbar : UserControl
{
    public SnipToolbar()
    {
        InitializeComponent();
        
        // Prevent pointer events from bubbling up to the canvas
        // Use Bubble strategy so child controls (buttons) receive clicks first
        // Then we mark the event as handled to stop it from reaching the canvas
        AddHandler(PointerPressedEvent, OnToolbarPointerPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }
    
    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Mark the event as handled so it doesn't bubble up to the canvas
        // This happens AFTER child controls have processed the event
        e.Handled = true;
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
