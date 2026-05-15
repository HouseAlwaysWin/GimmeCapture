using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Composition;

public static class RuntimeServiceFactory
{
    public sealed record SnipWindowShellServices(
        IScreenLayoutService ScreenLayoutService,
        IWindowLayerService WindowLayerService);

    public static IScreenCaptureService CreateScreenCaptureService()
    {
        return new WindowsScreenCaptureService(new AvaloniaWindowManager());
    }

    public static IWindowDetectionService CreateWindowDetectionService()
    {
        return new WindowDetectionService();
    }

    public static IDownloadWindowService CreateDownloadWindowService()
    {
        return new AvaloniaDownloadWindowService();
    }

    public static SnipWindowShellServices CreateSnipWindowShellServices()
    {
        return new SnipWindowShellServices(
            new AvaloniaScreenLayoutService(),
            new AvaloniaWindowLayerService());
    }

    public static ISnipWindowFactory CreateSnipWindowFactory()
    {
        var windowManager = new AvaloniaWindowManager();
        var shellServices = CreateSnipWindowShellServices();
        var screenCaptureService = CreateScreenCaptureService();
        var translationSessionServiceFactory = new TranslationSessionServiceFactory();
        var windowDetectionService = CreateWindowDetectionService();

        return new SnipWindowFactory(
            windowManager,
            shellServices.ScreenLayoutService,
            shellServices.WindowLayerService,
            screenCaptureService,
            translationSessionServiceFactory,
            windowDetectionService);
    }
}
