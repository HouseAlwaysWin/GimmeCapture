using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace GimmeCapture.Views
{
    public partial class UpdateDialog : Window
    {
        public bool Result { get; private set; }

        public UpdateDialog()
        {
            InitializeComponent();
        }

        public static async Task<bool> ShowDialog(Window owner, string message)
        {
            var dialog = new UpdateDialog();
            var textBlock = dialog.FindControl<TextBlock>("MessageText");
            if (textBlock != null) textBlock.Text = message;
            await dialog.ShowDialog(owner);
            return dialog.Result;
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Result = false;
            Close();
        }

        private void OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                this.BeginMoveDrag(e);
            }
        }
    }
}
