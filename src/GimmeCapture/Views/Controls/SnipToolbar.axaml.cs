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
}
