using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Linux;
#if WINDOWS
using GimmeCapture.Services.Platforms.Windows;
#endif

namespace GimmeCapture.Composition;

public static class RuntimeServiceFactory
{
    public sealed record SnipWindowShellServices(
        IScreenLayoutService ScreenLayoutService,
        IWindowLayerService WindowLayerService);

    public static IScreenCaptureService CreateScreenCaptureService()
    {
        // Windows uses GDI/WGC capture; other platforms fall back to the X11 backend
        // (docs/LINUX_PORT_FEASIBILITY.md, Phase 1).
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new WindowsScreenCaptureService(new AvaloniaWindowManager());
        }
#endif

        return new LinuxScreenCaptureService();
    }

    public static IWindowDetectionService CreateWindowDetectionService()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return new WindowDetectionService();
        }
#endif

        return new LinuxWindowDetectionService();
    }

    public static IDownloadWindowService CreateDownloadWindowService()
    {
        return new AvaloniaDownloadWindowService();
    }

    public static IToastService CreateToastService()
    {
        return new AvaloniaToastService();
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
