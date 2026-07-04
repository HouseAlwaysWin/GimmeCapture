using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Linux;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Composition;

public static class RuntimeServiceFactory
{
    public sealed record SnipWindowShellServices(
        IScreenLayoutService ScreenLayoutService,
        IWindowLayerService WindowLayerService);

    public static IScreenCaptureService CreateScreenCaptureService()
    {
        // Windows uses GDI/WGC capture; other platforms fall back to the placeholder backend until a
        // real X11/PipeWire capture is implemented (docs/LINUX_PORT_FEASIBILITY.md, Phase 1).
        if (OperatingSystem.IsWindows())
        {
            return new WindowsScreenCaptureService(new AvaloniaWindowManager());
        }

        return new LinuxScreenCaptureService();
    }

    public static IWindowDetectionService CreateWindowDetectionService()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowDetectionService();
        }

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
