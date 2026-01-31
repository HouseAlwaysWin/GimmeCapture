using Avalonia.Controls;

namespace GimmeCapture.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is GimmeCapture.ViewModels.MainWindowViewModel vm)
        {
            // Initialize Hotkey Service with this Window
            vm.HotkeyService.Initialize(this);

            vm.RequestCaptureAction = () =>
            {
                var snip = new SnipWindow();
                snip.DataContext = new GimmeCapture.ViewModels.SnipWindowViewModel();
                snip.Show();
            };
        }
    }
}