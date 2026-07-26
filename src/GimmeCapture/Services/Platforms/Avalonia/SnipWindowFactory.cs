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
            vm.AIResourceService,
            vm.OcrRuntimeService);
        var translationSelectionMonitor = new TranslationSelectionMonitor(
            _screenCaptureService,
            translationSession);
        var ocrEngineFactory = new PaddleOcrEngineFactory();
        var aiScanSessionService = new AIScanSessionService(
            _screenCaptureService,
            vm.AIResourceService,
            vm.SAM2RuntimeService,
            vm.OcrRuntimeService,
            vm.AppSettingsService,
            ocrEngineFactory);
        var quickOcrService = new QuickOcrService(
            new QuickOcrEngineProvider(
                vm.AIResourceService,
                vm.AppSettingsService,
                vm.OcrRuntimeService,
                ocrEngineFactory));

        var snipVm = new SnipWindowViewModel(
            vm.BorderColor,
            vm.BorderThickness,
            _screenCaptureService,
            _windowDetectionService,
            vm.RecordingService,
            vm,
            translationSession,
            translationSelectionMonitor,
            aiScanSessionService,
            quickOcrService,
            new AvaloniaCaptureVisibilityCoordinator(),
            ocrEngineFactory);

        snipVm.AutoActionMode = SnipWindowViewModel.ResolveAutoActionMode(mode);
        if (mode == CaptureMode.Record)
        {
            snipVm.CurrentMode = SnipMode.Recording;
        }
        else if (mode == CaptureMode.Translate)
        {
            snipVm.CurrentMode = SnipMode.Translation;
            snipVm.InitializeTranslationToolbarPosition();
        }
        else
        {
            // Screenshot/Snip: CurrentMode is already the default (Screenshot), so its setter never fires — position
            // the fixed top-center toolbar here so it's visible immediately on entry, before any selection is drawn
            // (matching Recording/Translation).
            snipVm.InitializeTranslationToolbarPosition();
        }

        // Freeze-frame: for a plain screenshot (not recording/translation/scrolling), snapshot the WHOLE desktop
        // BEFORE the overlay is shown, so shell "light dismiss" popups (tray flyout / Start menu / left-click
        // dropdowns) — which close the instant any full-screen overlay appears over them — are captured. The user
        // then selects/annotates on this frozen still (see FreezeScreenOnScreenshot). Grabbed here, before Show(),
        // so the overlay itself is never in the still (no self-exclusion needed).
        bool freezeApplies = vm.FreezeScreenOnScreenshot
            && (mode == CaptureMode.Normal || mode == CaptureMode.Copy || mode == CaptureMode.TextCopy || mode == CaptureMode.Pin)
            && snip.Width > 1 && snip.Height > 1;
        if (freezeApplies)
        {
            try
            {
                var grabOffset = snip.Position;
                double grabScaling = snip.Screens?.Primary?.Scaling ?? 1.0;
                var grabRegion = new global::Avalonia.Rect(0, 0, snip.Width, snip.Height);
                // CaptureScreenAsync awaits Task.Run internally; wrap in an outer Task.Run so blocking here (on the
                // UI thread) can't deadlock on the captured context. A fast GDI grab (~tens of ms) — an acceptable
                // pre-overlay pause, and it must complete before Show() so the still predates the overlay.
                var frozen = System.Threading.Tasks.Task.Run(() =>
                    _screenCaptureService.CaptureScreenAsync(grabRegion, grabOffset, grabScaling, includeCursor: false)
                ).GetAwaiter().GetResult();
                snipVm.SetFrozenScreenSnapshot(frozen, grabScaling);
            }
            catch (Exception ex)
            {
                GimmeCapture.Services.Core.Infrastructure.AppLog.Warning("SnipWindowFactory.FreezeGrab", ex);
            }
        }

        // Open without stealing foreground focus so focus-sensitive target UI (dropdowns, right-click context
        // menus) stays open and can be captured. ShowActivated must be set BEFORE Show(). Translation mode is
        // excluded (it needs real focus for its text controls) — see ShouldAvoidStealingFocus. In freeze-frame
        // mode ShouldAvoidStealingFocus is false (the still is already grabbed, so focus no longer matters).
        if (snipVm.ShouldAvoidStealingFocus)
        {
            snip.ShowActivated = false;
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
