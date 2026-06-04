using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace GimmeCapture.Views.Controls;

public partial class SnipToolbar : UserControl
{
    public SnipToolbar()
    {
        InitializeComponent();
        
        // Prevent pointer events from bubbling up to the canvas
        AddHandler(PointerPressedEvent, OnToolbarPointerPressed, Avalonia.Interactivity.RoutingStrategies.Bubble);

        var translateBtn = this.FindControl<Button>("TranslateAllButton");
        if (translateBtn != null)
        {
            translateBtn.Click += (s, e) => 
            {
                System.Diagnostics.Debug.WriteLine("[SnipToolbar] TranslateAllButton physically CLICKED");
            };
        }
    }
    
    private void OnToolbarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual)
        {
            var current = visual;
            while (current != null)
            {
                if (current is ComboBox || current is ContextMenu)
                {
                    return;
                }

                current = current.GetVisualParent();
            }
        }

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
