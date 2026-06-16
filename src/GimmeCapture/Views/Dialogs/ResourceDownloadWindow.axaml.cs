using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Dialogs
{
    public partial class ResourceDownloadWindow : Window
    {
        public ResourceDownloadWindow()
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
                this.BeginMoveDrag(e);
            }
        }
    }
}
