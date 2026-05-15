using Avalonia.Controls;
using System;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.OCR;
using GimmeCapture.Services.Translation;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Services.Platforms.Desktop;

public sealed class SnipWindowFactory : ISnipWindowFactory
{
    private readonly IWindowManager _windowManager;
    private readonly IScreenLayoutService _screenLayoutService;
    private readonly IWindowLayerService _windowLayerService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly ITranslationSessionServiceFactory _translationSessionServiceFactory;
    private readonly IWindowDetectionService _windowDetectionService;

    public SnipWindowFactory(
        IWindowManager windowManager,
        IScreenLayoutService screenLayoutService,
        IWindowLayerService windowLayerService,
        IScreenCaptureService screenCaptureService,
        ITranslationSessionServiceFactory translationSessionServiceFactory,
        IWindowDetectionService windowDetectionService)
    {
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
        _screenLayoutService = screenLayoutService ?? throw new ArgumentNullException(nameof(screenLayoutService));
        _windowLayerService = windowLayerService ?? throw new ArgumentNullException(nameof(windowLayerService));
        _screenCaptureService = screenCaptureService ?? throw new ArgumentNullException(nameof(screenCaptureService));
        _translationSessionServiceFactory = translationSessionServiceFactory ?? throw new ArgumentNullException(nameof(translationSessionServiceFactory));
        _windowDetectionService = windowDetectionService ?? throw new ArgumentNullException(nameof(windowDetectionService));
    }

    public void Open(object mainViewModel, CaptureMode mode)
    {
        ArgumentNullException.ThrowIfNull(mainViewModel);
        if (mainViewModel is not MainWindowViewModel vm)
        {
            throw new ArgumentException("Expected MainWindowViewModel.", nameof(mainViewModel));
        }

        var existing = _windowManager.FindWindowOfType<SnipWindow>();
        if (existing != null)
        {
            if (existing.DataContext is SnipWindowViewModel existingVm)
            {
                existingVm.HandleCaptureModeRequest(mode);
            }

            existing.Activate();
            return;
        }

        var snip = new SnipWindow(_screenLayoutService, _windowLayerService);
        ConfigureWindowBounds(snip);
        var translationSession = _translationSessionServiceFactory.Create(
            vm.AppSettingsService,
            vm.AIResourceService);
        var translationSelectionMonitor = new TranslationSelectionMonitor(
            _screenCaptureService,
            translationSession);
        var aiScanSessionService = new AIScanSessionService(
            _screenCaptureService,
            vm.AIResourceService,
            vm.SAM2RuntimeService,
            vm.AppSettingsService,
            new PaddleOcrEngineFactory());

        var snipVm = new SnipWindowViewModel(
            vm.BorderColor,
            vm.BorderThickness,
            vm.MaskOpacity,
            _screenCaptureService,
            _windowDetectionService,
            vm.RecordingService,
            vm,
            translationSession,
            translationSelectionMonitor,
            aiScanSessionService);

        snipVm.AutoActionMode = mode switch
        {
            CaptureMode.Copy => SnipAutoAction.Copy,
            CaptureMode.Pin => SnipAutoAction.Pin,
            CaptureMode.Record => SnipAutoAction.EnterRecordMode,
            _ => SnipAutoAction.None
        };
        if (mode == CaptureMode.Record)
        {
            snipVm.CurrentMode = SnipMode.Recording;
        }
        else if (mode == CaptureMode.Translate)
        {
            snipVm.CurrentMode = SnipMode.Translation;
            snipVm.InitializeTranslationToolbarPosition();
        }

        snip.DataContext = snipVm;
        snip.Show();
    }

    public object? GetActiveViewModel()
    {
        return _windowManager.FindWindowOfType<SnipWindow>()?.DataContext;
    }

    private void ConfigureWindowBounds(Window snip)
    {
        var allScreens = snip.Screens.All;
        if (allScreens.Count == 0)
        {
            return;
        }

        var screenBounds = allScreens.AsValueEnumerable().Select(s => s.Bounds).ToList();
        var primaryScreen = snip.Screens.Primary ?? allScreens.AsValueEnumerable().First();
        double unifiedScaling = primaryScreen.Scaling;

        if (_screenLayoutService.TryGetUnifiedDesktopPlacement(screenBounds, unifiedScaling, out var position, out var size))
        {
            snip.WindowStartupLocation = WindowStartupLocation.Manual;
            snip.Position = position;
            snip.Width = size.Width;
            snip.Height = size.Height;
        }
    }
}
