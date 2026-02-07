using Avalonia.Controls;
using Avalonia.Input;

namespace GimmeCapture.Views.Dialogs
{
    public partial class ResourceDownloadWindow : Window
    {
        public ResourceDownloadWindow()
        {
            InitializeComponent();
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
