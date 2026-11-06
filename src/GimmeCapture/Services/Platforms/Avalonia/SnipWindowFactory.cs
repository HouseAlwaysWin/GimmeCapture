using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Reactive.Linq;
using GimmeCapture.Composition;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.OCR;
using GimmeCapture.Services.Translation;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Floating;
using GimmeCapture.Views.Main;
using ReactiveUI;

namespace GimmeCapture.Services.Platforms.Desktop;

public sealed class SnipWindowFactory : ISnipWindowFactory, IDisposable
{
    private readonly IWindowManager _windowManager;
    private readonly IScreenLayoutService _screenLayoutService;
    private readonly IWindowLayerService _windowLayerService;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly ITranslationSessionServiceFactory _translationSessionServiceFactory;
    private readonly TranslationRuntimeService _translationRuntime;
    private readonly IWindowDetectionService _windowDetectionService;
    private IDisposable? _aiSwitchSubscription;
    private bool _openedOnce;

    /// <param name="translationRuntime">
    /// The translation model and cache every overlay's session shares, which this factory owns for the run: it frees
    /// the model when AI is turned off and disposes it with itself.
    /// </param>
    public SnipWindowFactory(
        IWindowManager windowManager,
        IScreenLayoutService screenLayoutService,
        IWindowLayerService windowLayerService,
        IScreenCaptureService screenCaptureService,
        ITranslationSessionServiceFactory translationSessionServiceFactory,
        TranslationRuntimeService translationRuntime,
        IWindowDetectionService windowDetectionService)
    {
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
        _screenLayoutService = screenLayoutService ?? throw new ArgumentNullException(nameof(screenLayoutService));
        _windowLayerService = windowLayerService ?? throw new ArgumentNullException(nameof(windowLayerService));
        _screenCaptureService = screenCaptureService ?? throw new ArgumentNullException(nameof(screenCaptureService));
        _translationSessionServiceFactory = translationSessionServiceFactory ?? throw new ArgumentNullException(nameof(translationSessionServiceFactory));
        _translationRuntime = translationRuntime ?? throw new ArgumentNullException(nameof(translationRuntime));
        _windowDetectionService = windowDetectionService ?? throw new ArgumentNullException(nameof(windowDetectionService));
    }

    public void Open(object mainViewModel, CaptureMode mode)
    {
        ArgumentNullException.ThrowIfNull(mainViewModel);
        if (mainViewModel is not MainWindowViewModel vm)
        {
            throw new ArgumentException("Expected MainWindowViewModel.", nameof(mainViewModel));
        }

        // Turning AI off frees the translation model now rather than at its idle unload: the switch is how a user asks
        // for that memory back. Watched from the first overlay on: the main view model first reaches this factory here,
        // and no model can have been loaded before. Freed off the UI thread, like SAM2's models on the same switch.
        _aiSwitchSubscription ??= vm.WhenAnyValue(x => x.EnableAI)
            .Skip(1)
            .Where(static enabled => !enabled)
            .Subscribe(_ => System.Threading.Tasks.Task.Run(_translationRuntime.ReleaseModel)
                .Forget("Settings.EnableAI.ReleaseTranslationModel"));

        _openedOnce = true;
        var existing = _windowManager.FindWindowOfType<SnipWindow>();
        if (existing != null)
        {
            if (existing.DataContext is SnipWindowViewModel existingVm)
            {
                existingVm.HandleCaptureModeRequest(mode);
            }

            // Consume the request stamp: this press re-targeted the overlay that is already open, so its wait
            // must not be reported later as the wait of whatever opens next.
            CaptureOpenTrace.TakeWaitedForUiThreadMs();
            existing.Activate();
            return;
        }

        // Stage timings for the span the log never had: the request -> the overlay actually on screen. This is
        // what a "pressed the hotkey and nothing happened" report gets checked against (see CaptureOpenTrace).
        double? waitedForUiThreadMs = CaptureOpenTrace.TakeWaitedForUiThreadMs();
        var openStopwatch = Stopwatch.StartNew();

        // A frozen screenshot's grab only reads the screens, so it starts now, on the pool, and the overlay's window and
        // view model are built meanwhile: they took 13-34 ms of a warm open and 300-360 of a run's first, before the
        // grab (47-88 ms warm) even began.
        bool freezeRequested = vm.FreezeScreenOnScreenshot
            && (mode == CaptureMode.Normal || mode == CaptureMode.Copy || mode == CaptureMode.TextCopy || mode == CaptureMode.Pin);
        var earlyGrab = freezeRequested ? StartDesktopGrab() : null;

        var snip = new SnipWindow(_screenLayoutService, _windowLayerService);
        ConfigureWindowBounds(snip);
        double afterWindowMs = openStopwatch.Elapsed.TotalMilliseconds;
        // One detector for the whole snip session, shared by translate mode and quick OCR: the auto-language probe
        // rebuilds ONNX sessions to compare recognisers, so it is cached per session rather than per capture.
        var scriptDetector = new OcrScriptDetector(vm.AIResourceService);
        var translationSession = _translationSessionServiceFactory.Create(
            vm.AppSettingsService,
            vm.AIResourceService,
            vm.OcrRuntimeService,
            scriptDetector);
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
                ocrEngineFactory),
            scriptDetector);
        double afterServicesMs = openStopwatch.Elapsed.TotalMilliseconds;

        var snipVm = new SnipWindowViewModel(
            vm.BorderColor,
            vm.BorderThickness,
            _screenCaptureService,
            _windowDetectionService,
            vm.RecordingService,
            translationSession,
            translationSelectionMonitor,
            aiScanSessionService,
            quickOcrService,
            new AvaloniaCaptureVisibilityCoordinator(),
            ocrEngineFactory,
            host: vm,
            services: new SnipWindowServices(
                vm.HotkeyService,
                vm.HotkeyRouterService,
                vm.AppSettingsService,
                vm.CaptureHistory,
                vm.AIResourceService,
                vm.OcrRuntimeService,
                vm.TranslationResultLayer,
                vm.SAM2RuntimeService,
                vm.BackgroundRemoval,
                vm.ResourceQueue,
                () => vm.FfmpegDownloader),
            settings: vm);

        snipVm.AutoActionMode = SnipWindowViewModel.ResolveAutoActionMode(mode);
        // Drawn on the processor, each frame of the overlay copies all of it to the screen, however little changed.
        snipVm.IsSoftwareRendered = StartupRenderer.IsSoftware;
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

        // The open-time OCR scan reads the whole overlay, as the frozen still or a live grab of the same area: get its
        // detector loading now, alongside the grab and the first show below.
        snipVm.PrepareOcrAutoScan();

        double afterViewModelMs = openStopwatch.Elapsed.TotalMilliseconds;

        // Freeze-frame: for a plain screenshot (not recording/translation/scrolling), snapshot the WHOLE desktop
        // BEFORE the overlay is shown, so shell "light dismiss" popups (tray flyout / Start menu / left-click
        // dropdowns) — which close the instant any full-screen overlay appears over them — are captured. The user
        // then selects/annotates on this frozen still (see FreezeScreenOnScreenshot). Grabbed here, before Show(),
        // so the overlay itself is never in the still (no self-exclusion needed).
        bool freezeApplies = freezeRequested && snip.Width > 1 && snip.Height > 1;
        string earlyGrabUse = earlyGrab == null ? "none" : "unused";
        if (freezeApplies)
        {
            try
            {
                var grab = new DesktopGrabArea(
                    snip.Position,
                    snip.Screens?.Primary?.Scaling ?? 1.0,
                    new global::Avalonia.Rect(0, 0, snip.Width, snip.Height));
                // The early grab, when it read the area the window covers; otherwise a grab of that area now. Waited for
                // on the UI thread on purpose: it must be done before Show() so the still predates the overlay. The grab
                // runs on the pool and cannot deadlock on this thread's context.
                var pending = earlyGrab is { } early && early.Area == grab ? early.Backdrop : GrabBackdrop(grab);
                if (ReferenceEquals(pending, earlyGrab?.Backdrop))
                {
                    earlyGrabUse = "used";
                }
                else
                {
                    earlyGrab?.Discard();
                }

                earlyGrab = null;
                // Null where it cannot be shown as a backdrop: the overlay then opens live.
                if (pending.GetAwaiter().GetResult() is { } backdrop)
                {
                    // Takes ownership.
                    snipVm.Surface.FreezeFromPreOverlayGrab(backdrop);
                }
            }
            catch (Exception ex)
            {
                GimmeCapture.Services.Core.Infrastructure.AppLog.Warning("SnipWindowFactory.FreezeGrab", ex);
            }
        }

        earlyGrab?.Discard();

        double afterFreezeGrabMs = openStopwatch.Elapsed.TotalMilliseconds;

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

        AppLog.Information(
            $"SnipOpen.Timings mode={mode} "
            + $"waitedForUiThread={(waitedForUiThreadMs is { } waited ? waited.ToString("F0") : "n/a")} "
            + $"window={afterWindowMs:F0} "
            + $"services={afterServicesMs - afterWindowMs:F0} "
            + $"viewModel={afterViewModelMs - afterServicesMs:F0} "
            + $"freezeGrab={afterFreezeGrabMs - afterViewModelMs:F0} "
            + $"show={openStopwatch.Elapsed.TotalMilliseconds - afterFreezeGrabMs:F0} "
            + $"total={openStopwatch.Elapsed.TotalMilliseconds:F0}ms "
            + $"earlyGrab={earlyGrabUse}");
    }

    public object? GetActiveViewModel()
    {
        return _windowManager.FindWindowOfType<SnipWindow>()?.DataContext;
    }

    /// <summary>At exit: frees the translation model, which outlives every overlay.</summary>
    public void Dispose()
    {
        _aiSwitchSubscription?.Dispose();
        _aiSwitchSubscription = null;
        _translationRuntime.Dispose();
    }

    /// <summary>
    /// The first overlay of a run used to pay for loading its own code — the window's XAML and the view model's JIT
    /// and reactive plumbing — about 0.45-0.65 s of UI thread before it appeared, against ~0.09 s for every later
    /// one. Building both once while the app is idle, unseen, took about half of that out of the first capture. What
    /// the first open still paid, in a published build, was mostly the window's wiring to its view model (63 ms against
    /// 1), the bindings that follow (36 against 11) and the first layout (19 against 3): code ReadyToRun leaves to the
    /// JIT, generic Rx and ReactiveUI pipelines over the app's own types first. So the unseen window is now also bound to
    /// an unseen view model and laid out at a screen's size, then let go of (<see cref="RehearseOverlay"/>), and the
    /// first overlay came up in 123-180 ms instead of 218-266. The first show itself, about 115 ms more before the UI
    /// thread is free, cannot be rehearsed without showing a window.
    /// </summary>
    public void ScheduleWarmUp(object mainViewModel, TimeSpan delay)
    {
        DispatcherTimer.RunOnce(() =>
        {
            // A real overlay has already loaded everything this would.
            if (!_openedOnce)
            {
                WarmUpOverlay();
            }

            if (mainViewModel is MainWindowViewModel vm)
            {
                Dispatcher.UIThread.Post(() => WarmUpPin(vm), DispatcherPriority.Background);
            }
        }, delay, DispatcherPriority.Background);
    }

    private void WarmUpOverlay()
    {
        var stopwatch = Stopwatch.StartNew();
        SnipWindow window;
        try
        {
            window = new SnipWindow(_screenLayoutService, _windowLayerService) { IsWarmUp = true };
        }
        catch (Exception ex)
        {
            AppLog.Warning("SnipWindowFactory.WarmUp", ex);
            return;
        }

        RehearseOverlay(window, () => _openedOnce, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Binds the unseen <paramref name="window"/> to a view model of its own and lays it out at a screen's size, then
    /// lets go of both: a step at a time, each posted at Background priority, so none holds the UI thread long. The
    /// parameterless view model has no main view model or services behind it, so it registers nothing; Dispose undoes
    /// what it set up. Ends early once <paramref name="realOverlayOpened"/> says a real overlay has opened. Logs how
    /// long each step held the UI thread, the window's build (<paramref name="buildMs"/>) included.
    /// </summary>
    internal static void RehearseOverlay(SnipWindow window, Func<bool> realOverlayOpened, double buildMs = 0)
    {
        SnipWindowViewModel? viewModel = null;
        // How long each step held the UI thread: the view model, the binding, the layout.
        var stepMs = new double[3];

        void Step(int step)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (realOverlayOpened())
                {
                    EndWarmUp(window, viewModel);
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    switch (step)
                    {
                        case 0:
                            viewModel = new SnipWindowViewModel();
                            break;
                        case 1:
                            window.DataContext = viewModel;
                            break;
                        default:
                            var screen = window.Screens.Primary;
                            var size = screen != null ? screen.Bounds.Size.ToSize(screen.Scaling) : new Size(1920, 1080);
                            window.Measure(size);
                            window.Arrange(new Rect(size));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("SnipWindowFactory.WarmUp", ex);
                    EndWarmUp(window, viewModel);
                    return;
                }

                stepMs[step] = stopwatch.Elapsed.TotalMilliseconds;
                if (step < stepMs.Length - 1)
                {
                    Step(step + 1);
                    return;
                }

                EndWarmUp(window, viewModel);
                AppLog.Information(
                    "Overlay.WarmUp",
                    $"build={buildMs:F0} viewModel={stepMs[0]:F0} bind={stepMs[1]:F0} layout={stepMs[2]:F0}ms");
            }, DispatcherPriority.Background);
        }

        Step(0);
    }

    // Unbinds first, which ends the window's subscriptions to the view model, as a real overlay's close does.
    private static void EndWarmUp(SnipWindow window, SnipWindowViewModel? viewModel)
    {
        try
        {
            window.DataContext = null;
            window.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warning("SnipWindowFactory.WarmUp", ex);
        }

        viewModel?.Dispose();
    }

    /// <summary>
    /// A frozen overlay stays up until the pin it opens has drawn, and the first pin of a run took 0.93-1.04 s to build
    /// and draw in a test build with the tray's host window up, later ones 40-70 ms (<c>PinOpenProbeTests</c>). Built
    /// first and thrown away, a pin took the next one to 0.32-0.51 s, keeping nothing; shown off screen until it had drawn
    /// as well, to 0.13 s, but that kept 26-28 MB more when drawing on the GPU (its cache and shaders, which later pins
    /// reuse) against 3-5 MB on the CPU. So it is shown only when the app draws on the CPU. Skipped while an overlay is
    /// open, so it never holds up a selection.
    /// </summary>
    private void WarmUpPin(MainWindowViewModel vm)
    {
        if (_windowManager.FindWindowOfType<SnipWindow>() != null)
        {
            return;
        }

        FloatingImageViewModel? pin = null;
        try
        {
            var picture = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            pin = new FloatingImageViewModel(
                picture, 64, 64, Colors.Red, 2.0, false, false, new ClipboardService(), vm.AIResourceService,
                vm.SAM2RuntimeService, vm.AppSettingsService, vm.BackgroundRemoval, vm.ResourceQueue);
            var window = new FloatingImageWindow
            {
                DataContext = pin,
                Position = new PixelPoint(-20000, -20000),
                ShowActivated = false,
            };
            if (!StartupRenderer.IsSoftware)
            {
                window.Close();
                pin.Dispose();
                return;
            }

            window.Show();
            // The first callback comes as the first frame starts, the second once it has been drawn. Closing the
            // window disposes its view model.
            window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ =>
                Dispatcher.UIThread.Post(window.Close, DispatcherPriority.Background)));
        }
        catch (Exception ex)
        {
            pin?.Dispose();
            AppLog.Warning("SnipWindowFactory.WarmUpPin", ex);
        }
    }

    // What a frozen screenshot grabs: the area the overlay window covers, as CaptureScreenAsync takes it.
    private readonly record struct DesktopGrabArea(global::Avalonia.PixelPoint Offset, double Scaling, global::Avalonia.Rect Region);

    private sealed record DesktopGrab(DesktopGrabArea Area, System.Threading.Tasks.Task<OverlayBackdrop?> Backdrop)
    {
        // Not used after all: its pixels go once the grab is done, and a grab that failed is no error of anyone's.
        public void Discard() =>
            Backdrop.ContinueWith(
                static grab =>
                {
                    if (grab.IsCompletedSuccessfully)
                    {
                        grab.Result?.Dispose();
                    }
                    else
                    {
                        _ = grab.Exception;
                    }
                },
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.None,
                System.Threading.Tasks.TaskScheduler.Default);
    }

    // The frozen screenshot's backdrop of `area`, made on the pool: the grab, and the copy the overlay shows, which was
    // made on the UI thread (6 ms for 3968x1160, 16 the first time) and is now the backdrop's only copy of the desktop.
    private System.Threading.Tasks.Task<OverlayBackdrop?> GrabBackdrop(DesktopGrabArea area) =>
        System.Threading.Tasks.Task.Run(async () => OverlayBackdrop.TryCreate(
            await _screenCaptureService.CaptureScreenAsync(area.Region, area.Offset, area.Scaling, includeCursor: false),
            area.Scaling,
            OverlayBackdrop.DefaultToDisplay));

    /// <summary>
    /// The area the overlay will cover, worked out as <see cref="ConfigureWindowBounds"/> does from the screens another
    /// window already knows (the main window, or the tray's host), and grabbed on the pool. Null where none can tell.
    /// </summary>
    private DesktopGrab? StartDesktopGrab()
    {
        try
        {
            var screens = _windowManager.GetMainWindow()?.Screens;
            var allScreens = screens?.All;
            if (screens == null || allScreens == null || allScreens.Count == 0)
            {
                return null;
            }

            var screenBounds = allScreens.AsValueEnumerable().Select(s => s.Bounds).ToList();
            double scaling = (screens.Primary ?? allScreens.AsValueEnumerable().First()).Scaling;
            if (!_screenLayoutService.TryGetUnifiedDesktopPlacement(screenBounds, scaling, out var position, out var size))
            {
                return null;
            }

            var area = new DesktopGrabArea(position, scaling, new global::Avalonia.Rect(0, 0, size.Width, size.Height));
            return new DesktopGrab(area, GrabBackdrop(area));
        }
        catch (Exception ex)
        {
            AppLog.Debug("SnipWindowFactory.EarlyFreezeGrab", ex);
            return null;
        }
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
