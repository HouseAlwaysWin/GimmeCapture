using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Controls;

public partial class ProcessingOverlay : UserControl
{
    public ProcessingOverlay()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
