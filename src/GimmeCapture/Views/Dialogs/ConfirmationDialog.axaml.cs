using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;

namespace GimmeCapture.Views.Dialogs
{
    public enum ConfirmationResult
    {
        Yes,
        No,
        Cancel
    }

    public partial class ConfirmationDialog : Window
    {
        public ConfirmationResult Result { get; private set; } = ConfirmationResult.Cancel;

        public ConfirmationDialog()
        {
            InitializeComponent();
        }

        public static async Task<ConfirmationResult> ShowConfirmation(Window owner)
        {
            var dialog = new ConfirmationDialog();
            await dialog.ShowDialog(owner);
            return dialog.Result;
        }

        private void OnYesClick(object? sender, RoutedEventArgs e)
        {
            Result = ConfirmationResult.Yes;
            Close();
        }

        private void OnNoClick(object? sender, RoutedEventArgs e)
        {
            Result = ConfirmationResult.No;
            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            Result = ConfirmationResult.Cancel;
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
