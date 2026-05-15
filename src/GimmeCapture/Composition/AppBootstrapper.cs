using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
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
        _mainWindowDependencies = MainWindowViewModelDependenciesFactory.CreateDefault();
        _downloadWindowService = RuntimeServiceFactory.CreateDownloadWindowService();
        _snipWindowFactory = RuntimeServiceFactory.CreateSnipWindowFactory();
    }

    public MainWindow CreateMainWindow()
    {
        return _mainWindow ??= new MainWindow(
            new MainWindowViewModel(_mainWindowDependencies),
            _downloadWindowService,
            _snipWindowFactory);
    }
}
