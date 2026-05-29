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

    public enum ConfirmationMode
    {
        YesNoCancel,
        YesNo,
        OkOnly
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

        public static readonly StyledProperty<ConfirmationMode> ModeProperty =
            AvaloniaProperty.Register<ConfirmationDialog, ConfirmationMode>(nameof(Mode), defaultValue: ConfirmationMode.YesNoCancel);

        public ConfirmationMode Mode
        {
            get => GetValue(ModeProperty);
            set => SetValue(ModeProperty, value);
        }

        public static readonly StyledProperty<string> PrimaryButtonTextProperty =
            AvaloniaProperty.Register<ConfirmationDialog, string>(nameof(PrimaryButtonText), defaultValue: "Yes");

        public string PrimaryButtonText
        {
            get => GetValue(PrimaryButtonTextProperty);
            set => SetValue(PrimaryButtonTextProperty, value);
        }

        public static readonly StyledProperty<bool> IsExtendedModeProperty =
            AvaloniaProperty.Register<ConfirmationDialog, bool>(nameof(IsExtendedMode), defaultValue: true);

        public bool IsExtendedMode
        {
            get => GetValue(IsExtendedModeProperty);
            set => SetValue(IsExtendedModeProperty, value);
        }

        public static readonly StyledProperty<bool> IsNoVisibleProperty =
            AvaloniaProperty.Register<ConfirmationDialog, bool>(nameof(IsNoVisible), defaultValue: true);

        public bool IsNoVisible
        {
            get => GetValue(IsNoVisibleProperty);
            set => SetValue(IsNoVisibleProperty, value);
        }

        public ConfirmationDialog()
        {
            InitializeComponent();
            DataContext = this;
            UpdateModeUI();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ModeProperty)
            {
                UpdateModeUI();
            }
        }

        private void UpdateModeUI()
        {
            PrimaryButtonText = Mode == ConfirmationMode.OkOnly 
                ? LocalizationService.Instance["UpdateBtnOk"] 
                : LocalizationService.Instance["Yes"];
            IsExtendedMode = Mode == ConfirmationMode.YesNoCancel;
            IsNoVisible = Mode != ConfirmationMode.OkOnly;
        }

        public static async Task<ConfirmationResult> ShowConfirmation(Window owner)
        {
            var loc = LocalizationService.Instance;
            return await ShowConfirmation(owner, loc["UnsavedTitle"], loc["UnsavedMessage"]);
        }

        public static async Task<ConfirmationResult> ShowConfirmation(Window owner, string title, string message, ConfirmationMode mode = ConfirmationMode.YesNoCancel, WindowStartupLocation? startupLocation = null)
        {
            var dialog = new ConfirmationDialog
            {
                DialogTitle = title,
                DialogMessage = message,
                Mode = mode
            };
            if (startupLocation.HasValue)
            {
                dialog.WindowStartupLocation = startupLocation.Value;
            }
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

