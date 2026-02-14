using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;
using Avalonia;

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
        
        public static readonly StyledProperty<string> DialogTitleProperty =
            AvaloniaProperty.Register<ConfirmationDialog, string>(nameof(DialogTitle), defaultValue: "");

        public string DialogTitle
        {
            get => GetValue(DialogTitleProperty);
            set => SetValue(DialogTitleProperty, value);
        }

        public static readonly StyledProperty<string> DialogMessageProperty =
            AvaloniaProperty.Register<ConfirmationDialog, string>(nameof(DialogMessage), defaultValue: "");

        public string DialogMessage
        {
            get => GetValue(DialogMessageProperty);
            set => SetValue(DialogMessageProperty, value);
        }

        public ConfirmationDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        public static async Task<ConfirmationResult> ShowConfirmation(Window owner)
        {
            var loc = Services.Core.LocalizationService.Instance;
            return await ShowConfirmation(owner, loc["UnsavedTitle"], loc["UnsavedMessage"]);
        }

        public static async Task<ConfirmationResult> ShowConfirmation(Window owner, string title, string message)
        {
            var dialog = new ConfirmationDialog
            {
                DialogTitle = title,
                DialogMessage = message
            };
            await dialog.ShowDialog(owner);
            return dialog.Result;
        }

        private void OnYesClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Result = ConfirmationResult.Yes;
            Close();
        }

        private void OnNoClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Result = ConfirmationResult.No;
            Close();
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
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
