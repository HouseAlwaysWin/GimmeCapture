using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Shared;

public partial class RecordingProgressWindow : Window
{
    public RecordingProgressWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
