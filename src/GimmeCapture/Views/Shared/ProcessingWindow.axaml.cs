using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace GimmeCapture.Views.Shared
{
    public partial class ProcessingWindow : Window
    {
        public ProcessingWindow()
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
