using Avalonia.Controls;

namespace GimmeCapture.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        this.PropertyChanged += OnPropertyChanged;
    }

    private void OnPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Minimize to Tray
                Hide();
            }
        }
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