using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Controls;

public partial class DrawingToolbar : UserControl
{
    public DrawingToolbar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
