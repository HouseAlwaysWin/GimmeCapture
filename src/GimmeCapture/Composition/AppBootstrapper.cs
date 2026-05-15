using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Composition;

public sealed class AppBootstrapper
{
    private readonly MainWindowViewModelDependencies _mainWindowDependencies;
    private readonly IDownloadWindowService _downloadWindowService;
    private readonly ISnipWindowFactory _snipWindowFactory;
    private MainWindow? _mainWindow;

    public AppBootstrapper()
    {
        _mainWindowDependencies = MainWindowViewModelDependencies.CreateDefault();

        var screenLayoutService = new AvaloniaScreenLayoutService();
        var windowLayerService = new AvaloniaWindowLayerService();
        var screenCaptureService = new WindowsScreenCaptureService(_mainWindowDependencies.WindowManager);

        _downloadWindowService = new AvaloniaDownloadWindowService();
        _snipWindowFactory = new SnipWindowFactory(
            _mainWindowDependencies.WindowManager,
            screenLayoutService,
            windowLayerService,
            screenCaptureService);
    }

    public MainWindow CreateMainWindow()
    {
        return _mainWindow ??= new MainWindow(
            new MainWindowViewModel(_mainWindowDependencies),
            _downloadWindowService,
            _snipWindowFactory);
    }
}
