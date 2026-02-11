using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Controls;

public partial class TopLoadingBar : UserControl
{
    public TopLoadingBar()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
