using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;
using GimmeCapture.Services.Core;

namespace GimmeCapture.Views.Dialogs
{
    public partial class UpdateDialog : Window
    {
        public bool Result { get; private set; }

        public UpdateDialog()
        {
            InitializeComponent();
        }

        public static async Task<bool> ShowDialog(Window owner, string message, bool isUpdateAvailable = true)
        {
            var dialog = new UpdateDialog();
            var textBlock = dialog.FindControl<TextBlock>("MessageText");
            if (textBlock != null) textBlock.Text = message;
            
            dialog.UpdateUI(isUpdateAvailable);

            await dialog.ShowDialog(owner);
            return dialog.Result;
        }

        private void UpdateUI(bool isUpdateAvailable)
        {
            var updateBtn = this.FindControl<Button>("UpdateButton");
            var cancelBtn = this.FindControl<Button>("CancelButton");
            var okBtn = this.FindControl<Button>("OkButton");

            var loc = LocalizationService.Instance;

            if (updateBtn != null) 
            {
                updateBtn.IsVisible = isUpdateAvailable;
                updateBtn.Content = loc["UpdateBtnConfirm"];
            }
            if (cancelBtn != null) 
            {
                cancelBtn.IsVisible = isUpdateAvailable;
                cancelBtn.Content = loc["UpdateBtnCancel"];
            }
            if (okBtn != null) 
            {
                okBtn.IsVisible = !isUpdateAvailable;
                okBtn.Content = loc["UpdateBtnOk"];
            }
        }

        private void OnUpdateClick(object? sender, RoutedEventArgs e)
        {
            Result = true;
            Close();
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            Result = false; // Just close
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
