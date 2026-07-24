using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using GimmeCapture.Views.Main;
using GimmeCapture.Views.Shared;
using GimmeCapture.Models;
using System;
using Avalonia.Platform;
using Avalonia.Input.Raw;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;
using ReactiveUI;
using Avalonia.Interactivity;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace GimmeCapture.Views.Main;

// OnDataContextChanged: wires the SnipWindowViewModel's action delegates (close/hide/show, pin/record
// windows, dialogs, capture affinity, etc.) to this window. Split out of SnipWindow.axaml.cs
// (god class reduction) — no behavior change.
public partial class SnipWindow : Window
{
    private ScrollingCaptureHintWindow? _scrollHintWindow;
    private ScrollingCaptureRegionWindow? _scrollRegionWindow;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _selectionRectSubscription?.Dispose();
        _selectionRectSubscription = null;
        _viewportBoundsSubscription?.Dispose();
        _viewportBoundsSubscription = null;
        _toolbarBoundsSubscription?.Dispose();
        _toolbarBoundsSubscription = null;
        _recordingStateSubscription?.Dispose();
        _recordingStateSubscription = null;
        _translationModeSubscription?.Dispose();
        _translationModeSubscription = null;
        ResetTranslationSelectionModifierState();

        _viewModel = DataContext as SnipWindowViewModel;
        if (_viewModel != null)
        {
            var vm = _viewModel;
            _translationModeSubscription = vm.WhenAnyValue(x => x.IsTranslationMode)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(isTranslationMode =>
                {
                    if (!isTranslationMode)
                    {
                        ResetTranslationSelectionModifierState();
                    }
                });

            _viewportBoundsSubscription = this.GetObservable(Visual.BoundsProperty)
                .Subscribe(b => vm.ViewportSize = b.Size);
            
            // Sync Toolbar size to VM for adaptive positioning
            _toolbarBoundsSubscription = this.Toolbar.GetObservable(Visual.BoundsProperty).Subscribe(b =>
            {
                vm.ToolbarWidth = b.Width;
                vm.ToolbarHeight = b.Height;
            });

            // WDA_EXCLUDEFROMCAPTURE: translation OCR / recording without annotations exclude SnipWindow from
            // FFmpeg gdigrab so output matches full SelectionRect without chrome; annotations require capturable window + inset crop (see RecordingUsesWindowsExcludeFromCapture).
            vm.SyncRecordingScreenCaptureAffinity = () => ApplyRecordingScreenCaptureAffinity(vm);
            if (vm.RecordingService != null)
            {
                _recordingStateSubscription = Observable.Merge(
                        vm.RecordingService.WhenAnyValue(x => x.State).Select(_ => 0),
                        vm.WhenAnyValue(x => x.RecordingUsesWindowsExcludeFromCapture).Select(_ => 0),
                        vm.WhenAnyValue(x => x.IsTranslationMode).Select(_ => 0))
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        ApplyRecordingScreenCaptureAffinity(vm);
                        vm.RaisePropertyChanged(nameof(vm.HideSelectionDecoration));
                        vm.RaisePropertyChanged(nameof(vm.HideFrameBorder));
                        vm.RaisePropertyChanged(nameof(vm.IsToolbarVisible));
                        vm.RaisePropertyChanged(nameof(vm.IsToolbarShownOnScreen));
                    });
            }

            ApplyRecordingScreenCaptureAffinity(vm);

            _viewModel.IsMagnifierEnabled = true;
            _viewModel.CloseAction = () =>
            {
                AppLog.Information($"SnipWindow.CloseAction → Close() (IsVisible={IsVisible})");
                Close();
            };

            _viewModel.ForceClearSelectionRegionAction = () =>
            {
                try
                {
                    // Collapse the selection-border region to the 1×1 click-through stub immediately (not the
                    // throttled binding), and force a repaint so no stale yellow ring remains on screen.
                    UpdateWindowRegion(default, SnipState.Idle, isDrawingMode: false);
                    this.InvalidateVisual();
                    AppLog.Information("SnipWindow.ForceClearSelectionRegion applied");
                }
                catch (Exception ex)
                {
                    AppLog.Warning("SnipWindow.ForceClearSelectionRegion", ex);
                }
            };

            _viewModel.CloseStaleOverlayWindowsAction = () =>
            {
                try
                {
                    if (Avalonia.Application.Current?.ApplicationLifetime
                        is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        return;
                    }

                    // Snapshot first — Close() mutates desktop.Windows. Log every window's managed .NET type (the
                    // Win32 class GUID is per-instance and useless), then close any VISIBLE overlay that isn't the
                    // main window, a floating pin, or this one — that catches the leftover selection-frame overlay
                    // (yellow) and the countdown/toast outline (red) regardless of their exact type.
                    var windows = new System.Collections.Generic.List<Window>(desktop.Windows);
                    foreach (var w in windows)
                    {
                        var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

                        // Log full geometry so the leftover frame can be identified (a bare base Window could be the
                        // off-screen tray/hotkey host — which must NEVER be closed).
                        AppLog.Information($"SnipWindow.OverlayScan hwnd=0x{hwnd.ToInt64():X} type={w.GetType().Name} vis={w.IsVisible} pos=({w.Position.X},{w.Position.Y}) size={w.Width}x{w.Height} title='{w.Title}'");

                        // SAFE close: only the known transient capture/recording outline overlays (yellow region
                        // outline, red countdown/hint). Never base Window (tray host), SnipWindow, main, or pins.
                        if (w is ScrollingCaptureRegionWindow
                            || w is ScrollingCaptureHintWindow
                            || w is CaptureCountdownWindow
                            || w is ToastWindow)
                        {
                            AppLog.Information($"SnipWindow.CloseStaleOverlay closing {w.GetType().Name} hwnd=0x{hwnd.ToInt64():X}");
                            try { w.Close(); } catch { /* best effort */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("SnipWindow.CloseStaleOverlay", ex);
                }
            };

            _viewModel.HideAction = () => Hide();
            _viewModel.ShowAction = () => Show();

            // Manual scrolling-capture chrome: a region outline + a hint, both capture-excluded,
            // click-through / non-stealing so they show the region without entering the capture
            // or blocking the user's scrolling.
            _viewModel.ShowScrollingHintAction = () =>
            {
                double scaling = _viewModel.VisualScaling <= 0 ? 1.0 : _viewModel.VisualScaling;
                var r = _viewModel.SelectionRect;
                int px = (int)(r.X * scaling) + _viewModel.ScreenOffset.X;
                int py = (int)(r.Y * scaling) + _viewModel.ScreenOffset.Y;
                // Physical rectangle of the captured region — used to anchor the hint just outside it.
                var anchor = new PixelRect(px, py, (int)(r.Width * scaling), (int)(r.Height * scaling));

                if (_scrollRegionWindow == null)
                {
                    // Effective outline thickness (matches the window ctor's Math.Max(2, ...)).
                    double borderThickness = Math.Max(2, _viewModel.SelectionBorderThickness);
                    int regionX = px, regionY = py;
                    double regionW = r.Width, regionH = r.Height;

                    if (OperatingSystem.IsLinux())
                    {
                        // X11 can't exclude our window from the screen grab, so an outline drawn on the
                        // SelectionRect edge would stitch into the image as red seams. Inflate the window
                        // outward (physical px, +1 so sub-pixel rounding never bleeds red into the capture)
                        // so the outline sits in a margin JUST OUTSIDE the captured rect; the transparent
                        // centre then fully contains SelectionRect, keeping the capture clean while the
                        // region is still outlined.
                        int inflate = (int)Math.Ceiling(borderThickness * scaling) + 1;
                        regionX = px - inflate;
                        regionY = py - inflate;
                        regionW = r.Width + (2 * inflate / scaling);
                        regionH = r.Height + (2 * inflate / scaling);
                    }

                    _scrollRegionWindow = new ScrollingCaptureRegionWindow(
                        borderThickness, _viewModel.SelectionBorderColor)
                    {
                        Position = new PixelPoint(regionX, regionY),
                        Width = regionW,
                        Height = regionH
                    };
                    _scrollRegionWindow.Show();
                }

                if (_scrollHintWindow == null)
                {
                    var hint = LocalizationService.Instance["ScrollingHintText"];
                    var stalled = LocalizationService.Instance["ScrollingHintStalled"];
                    var finishLabel = LocalizationService.Instance["ScrollingFinish"];
                    var cancelLabel = LocalizationService.Instance["Cancel"];
                    _scrollHintWindow = new ScrollingCaptureHintWindow(
                        hint,
                        stalled,
                        finishLabel,
                        cancelLabel,
                        anchor,
                        () => _viewModel.FinishManualScrollCapture(cancelled: false),
                        () => _viewModel.FinishManualScrollCapture(cancelled: true));
                    _scrollHintWindow.Show();
                }
            };
            _viewModel.UpdateScrollingHintAction = rows => _scrollHintWindow?.UpdateHint(rows);
            _viewModel.UpdateScrollingStallAction = stalled => _scrollHintWindow?.SetStalled(stalled);
            _viewModel.HideScrollingHintAction = () =>
            {
                _scrollHintWindow?.Close();
                _scrollHintWindow = null;
                _scrollRegionWindow?.Close();
                _scrollRegionWindow = null;
            };

            // Own the dialog to the snip overlay so it appears above the topmost overlay and the
            // overlay's hit-test region is disabled while modal (otherwise clicks are swallowed).
            _viewModel.ShowOkDialogAction = async (title, message) =>
            {
                await GimmeCapture.Views.Dialogs.ConfirmationDialog.ShowConfirmation(
                    this, title, message, GimmeCapture.Views.Dialogs.ConfirmationMode.OkOnly);
            };

            _viewModel.OpenRecordingProgressWindowAction = () =>
            {
                if (_progressWindow != null) return;
                
                _progressWindow = new RecordingProgressWindow
                {
                    DataContext = _viewModel,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _progressWindow.Show();

                // Recording is finished by the time finalize starts, so drop the capture-exclusion
                // (WDA_EXCLUDEFROMCAPTURE) BEFORE hiding. Hiding a still-excluded, regioned overlay leaves a DWM
                // ghost of the yellow selection frame on screen (it survives the hide and even the window close).
                // See docs/WGC_HANDOFF.md.
                var snipHwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (snipHwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                {
                    Win32Helpers.SetWindowCaptureVisibility(snipHwnd, visible: true);
                    Win32Helpers.ClearWindowRegion(snipHwnd);
                }

                Hide(); // Hide main window to allow user interaction
            };

            _viewModel.CloseRecordingProgressWindowAction = () =>
            {
                if (_progressWindow != null)
                {
                    _progressWindow.Close();
                    _progressWindow = null;
                }

                // Show main window back after finalization (e.g. for file picker)
                // Unless it was already closed/closing
                if (this.IsVisible)
                {
                    Show();
                }
            };

            // Standalone processing spinner for flows that hide the snip overlay to grab a clean capture and then
            // run a long background task (Quick-OCR text copy). Unlike the recording progress window, the caller
            // already hid the snip window and manages its own show/close, so this only shows/closes the themed
            // spinner — no capture-affinity or Hide()/Show() bookkeeping. Reuses the same generic spinner view.
            _viewModel.ShowProcessingWindowAction = () =>
            {
                if (_processingWindow != null) return;

                _processingWindow = new RecordingProgressWindow
                {
                    DataContext = _viewModel,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _processingWindow.Show();
            };

            _viewModel.HideProcessingWindowAction = () =>
            {
                if (_processingWindow != null)
                {
                    _processingWindow.Close();
                    _processingWindow = null;
                }
            };
            
            // Subscribe to selection and state changes to update the native interaction region.
            // Split into two subscriptions if arguments exceed 7 to avoid compilation error
            var trigger1 = vm.WhenAnyValue(
                x => x.InteractionRegionRevision,
                x => x.SelectionRect, 
                x => x.CurrentState, 
                x => x.IsDrawingMode,
                x => x.IsTranslationMode,
                x => x.IsTranslationSelectionActive,
                x => x.RecState);
                
            var trigger2 = vm.WhenAnyValue(
                x => x.ToolbarWidth,
                x => x.ToolbarHeight,
                x => x.ShowToolbar,
                x => x.IsToolbarVisible,
                x => x.IsToolbarShownOnScreen,
                x => x.CurrentTranslationTool);

            var trigger3 = vm.WhenAnyValue(
                    x => x.ToolbarLeft,
                    x => x.ToolbarTop,
                    x => x.TranslationToolbarLeft,
                    x => x.TranslationToolbarTop)
                .Select(_ => 0)
                .StartWith(0);

            // Recompute Win32 region when translation boxes are added/removed (not covered by SelectionRect alone).
            var userSelectionsChanged = Observable
                .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => vm.UserSelections.CollectionChanged += h,
                    h => vm.UserSelections.CollectionChanged -= h)
                .Select(_ => 0)
                .StartWith(0);

            var translatedBlocksChanged = Observable
                .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => vm.TranslatedBlocks.CollectionChanged += h,
                    h => vm.TranslatedBlocks.CollectionChanged -= h)
                .Select(_ => 0)
                .StartWith(0);

            var translationOverlayChanged = vm.WhenAnyValue(
                    x => x.TranslationOverlayLeft,
                    x => x.TranslationOverlayTop)
                .Select(_ => 0)
                .StartWith(0);

            _selectionRectSubscription = Observable.CombineLatest(
                    trigger1,
                    trigger2,
                    trigger3,
                    userSelectionsChanged,
                    translatedBlocksChanged,
                    translationOverlayChanged,
                    (t1, t2, _, __, ___, ____) => t1)
                .Throttle(TimeSpan.FromMilliseconds(16))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(tuple => UpdateWindowRegion(tuple.Item2, tuple.Item3, tuple.Item4));
            
            _viewModel.FocusWindowAction = () =>
            {
                this.Activate();
                this.Focus();
            };

            _viewModel.PersistTranslationSelectionsAction = PersistTranslatedSelectionsToDetachedLayer;

            _viewModel.CaptureDrawingModeSnapshotAsync = async () =>
            {
                var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                bool restoreCaptureVisibility = false;

                try
                {
                    if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
                        restoreCaptureVisibility = true;
                        await Task.Delay(50);
                    }

                    return await vm.CaptureRegionBitmapAsync();
                }
                finally
                {
                    if (restoreCaptureVisibility && hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: true);
                    }
                }
            };

            // Per-capture exclusion for the full-screen translation OCR grab. The overlay is NOT excluded from
            // capture continuously anymore (that grayed a Chromium window underneath — see
            // ApplyRecordingScreenCaptureAffinity); instead we exclude it only for the ~50 ms of the grab so the
            // OCR image doesn't include the overlay's own chrome, then restore WDA_NONE. Same shape as
            // CaptureDrawingModeSnapshotAsync above.
            _viewModel.RunTranslationOcrGrabExcludedAsync = async grab =>
            {
                var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                bool restoreCaptureVisibility = false;

                try
                {
                    if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
                        restoreCaptureVisibility = true;
                        await Task.Delay(50);
                    }

                    return await grab();
                }
                finally
                {
                    if (restoreCaptureVisibility && hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: true);
                    }
                }
            };

            _viewModel.PickSaveFileAction = async () =>
            {
                 var topLevel = TopLevel.GetTopLevel(this);
                 if (topLevel == null) return null;
                 
                 bool isRecording = _viewModel.IsRecordingMode;
                 string defaultExt = isRecording ? _viewModel.RecordFormat : "png";
                 string fileTypeName = isRecording ? $"{defaultExt.ToUpper()} Video" : "PNG Image";
                 string pattern = $"*.{defaultExt}";
                 
                 var fileChoices = new System.Collections.Generic.List<Avalonia.Platform.Storage.FilePickerFileType>();
                 if (isRecording)
                 {
                     fileChoices.Add(GimmeCapture.Views.Shared.VideoFilePickerTypes.SaveVideos);
                     fileChoices.Add(GimmeCapture.Views.Shared.VideoFilePickerTypes.AllFiles);
                 }
                 else
                 {
                     fileChoices.Add(new Avalonia.Platform.Storage.FilePickerFileType(fileTypeName) { Patterns = new[] { pattern } });
                 }

                 var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                 {
                     Title = isRecording ? "Save Recording" : "Save Screenshot",
                     DefaultExtension = defaultExt,
                     ShowOverwritePrompt = true,
                    SuggestedFileName = CaptureFileNameService.SuggestedBaseName(_viewModel.MainVm?.FileNameTemplate),
                     FileTypeChoices = fileChoices
                 });
                 
                 return file?.Path.LocalPath;
            };

            _viewModel.OpenPinnedVideoWindowAction = (recordingPath, pixelWidth, pixelHeight, originalWidth, originalHeight, color, thickness, hideDecoration, hideBorder) =>
            {
                var vm = new FloatingVideoViewModel(
                    recordingPath,
                    pixelWidth,
                    pixelHeight,
                    originalWidth,
                    originalHeight,
                    color,
                    thickness,
                    hideDecoration,
                    hideBorder,
                    _clipboardService,
                    _viewModel.MainVm?.AppSettingsService);

                // Copying a clip from the pin persists a managed copy into History (like image copy),
                // so the copied/trimmed video shows up in the history panel.
                var captureHistory = _viewModel.MainVm?.CaptureHistory;
                vm.AddClipToHistoryAsync = async (clipPath, w, h) =>
                {
                    try
                    {
                        if (captureHistory == null
                            || !(_viewModel.MainVm?.EnableHistory ?? false)
                            || string.IsNullOrEmpty(clipPath)
                            || !System.IO.File.Exists(clipPath))
                        {
                            return;
                        }

                        string ext = System.IO.Path.GetExtension(clipPath).TrimStart('.');
                        if (string.IsNullOrEmpty(ext)) ext = "mp4";
                        string managed = captureHistory.CreateManagedCapturePath(ext);
                        System.IO.File.Copy(clipPath, managed, true);
                        await captureHistory.AddVideoAsync(managed, w, h, GimmeCapture.Models.CaptureHistorySource.PlainCopy);
                    }
                    catch (Exception ex)
                    {
                        GimmeCapture.Services.Core.Infrastructure.AppLog.Warning("FloatingVideo.AddCopyToHistory", ex);
                    }
                };

                // Freeze the current video frame into a plain image pin (no AI): it's just the current
                // frame pinned out. The user can still enter point-removal / background-removal from the
                // pinned image's own toolbar if they want to cut something out.
                vm.FreezeFrameToImagePinAction = bitmap =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        _viewModel.OpenPinWindowAction?.Invoke(
                            bitmap,
                            new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height),
                            vm.BorderColor,
                            vm.BorderThickness,
                            false, // runAI (auto background removal)
                            false, // initialInteractive (do NOT auto-enter SAM2 point-removal)
                            null,  // pinnedText
                            0.0,   // inferredFontSize
                            null)); // scrollableContentSize (not a scrolling pin)
                };

                var padding = vm.WindowPadding;
                var window = new FloatingVideoWindow
                {
                    DataContext = vm,
                    Width = originalWidth + padding.Left + padding.Right,
                    Height = originalHeight + padding.Top + padding.Bottom,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                window.Show();
            };

            _viewModel.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI, initialInteractive, pinnedText, inferredFontSize, scrollableContentSize) =>
            {
                // Use settings directly from MainVm to ensure consistency
                bool hideDecoration = _viewModel.MainVm?.HideSnipPinDecoration ?? false;
                bool hideBorder = _viewModel.MainVm?.HideSnipPinBorder ?? false;
                var aiService = _viewModel.MainVm?.AIResourceService;
                
                if (aiService == null)
                {
                     // Fallback check (shouldn't happen if MainVm is set)
                     System.Diagnostics.Debug.WriteLine("AIResourceService is null!");
                     return;
                }
                
                if (_viewModel.MainVm == null) return;
                // The window (DisplayWidth/Height) is `rect` in both cases. For a long scrolling-capture pin the
                // rect is the original selection and `scrollableContentSize` (the full stitch) only sets the
                // fit-to-width scrolling content — the window still resizes freely like any other pin.
                bool scrolling = scrollableContentSize.HasValue;
                var vm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, hideDecoration, hideBorder, _clipboardService, aiService, _viewModel.MainVm.SAM2RuntimeService, _viewModel.MainVm.AppSettingsService, _viewModel.MainVm.AIPathService, _viewModel.MainVm.ResourceQueue, pinnedText, inferredFontSize);
                vm.WingScale = _viewModel.WingScale;
                if (scrolling)
                {
                    vm.ConfigureScrollableContent(scrollableContentSize!.Value.Width, scrollableContentSize.Value.Height);
                }
                
                try
                {
                    // Calculate Window Size & Position based on the padding needed for decorations
                    // The 'rect' is the IMAGE position/size in Logical pixels.
                    // Window Position must be in PHYSICAL pixels.
                    double scaling = _viewModel.VisualScaling;
                    var padding = vm.WindowPadding;

                    // Clamp oversized pins (e.g. a tall scrolling-capture stitch) to fit the
                    // target screen. Without this the window opens larger than the screen and its
                    // bottom/corner resize handles are off-screen, so it can't be resized.
                    double screenW = 0, screenH = 0, scrLeft = 0, scrTop = 0;
                    bool haveScreen = false;
                    if (_viewModel.AllScreenBounds != null)
                    {
                        foreach (var s in _viewModel.AllScreenBounds)
                        {
                            if (new Rect(s.X, s.Y, s.W, s.H).Intersects(rect))
                            {
                                screenW = s.W; screenH = s.H; scrLeft = s.X; scrTop = s.Y; haveScreen = true;
                                break;
                            }
                        }

                        if (!haveScreen && _viewModel.AllScreenBounds.Count > 0)
                        {
                            var s = _viewModel.AllScreenBounds[0];
                            screenW = s.W; screenH = s.H; scrLeft = s.X; scrTop = s.Y; haveScreen = true;
                        }
                    }

                    bool clampedToScreen = false;
                    if (haveScreen)
                    {
                        double contentW = vm.DisplayWidth + padding.Left + padding.Right;
                        double contentH = vm.DisplayHeight + padding.Top + padding.Bottom;
                        // Scale the displayed window down to fit the target screen (aspect preserved). Only
                        // DisplayWidth/Height change; vm.Image stays full resolution (and a scrolling pin's
                        // content re-fits its width), so save/copy/export are unaffected. Pure math lives in
                        // PinFitMath so it can be unit tested without a UI thread.
                        double fit = PinFitMath.ComputeFitScale(contentW, contentH, screenW, screenH);
                        if (fit < 1.0)
                        {
                            vm.DisplayWidth *= fit;
                            vm.DisplayHeight *= fit;
                            clampedToScreen = true;
                        }
                    }

                    // Convert Logical Rect to Physical Screen coordinates
                    int physicalX = (int)(rect.X * scaling) + _viewModel.ScreenOffset.X;
                    int physicalY = (int)(rect.Y * scaling) + _viewModel.ScreenOffset.Y;

                    // Convert Logical Padding to Physical
                    int physicalPaddingLeft = (int)(padding.Left * scaling);
                    int physicalPaddingTop = (int)(padding.Top * scaling);

                    // A clamped pin is placed at the target screen's top-left (+margin) so the whole
                    // window, including every resize handle, is on-screen. Normal pins stay anchored
                    // to the selection.
                    var pinPosition = clampedToScreen
                        ? new PixelPoint(
                            (int)(scrLeft * scaling) + _viewModel.ScreenOffset.X + 20,
                            (int)(scrTop * scaling) + _viewModel.ScreenOffset.Y + 20)
                        : new PixelPoint(physicalX - physicalPaddingLeft, physicalY - physicalPaddingTop);

                    // Create Window
                    var win = new FloatingImageWindow
                    {
                        DataContext = vm,
                        // Set physical position using converted values
                        Position = pinPosition,
                        // Width/Height in Avalonia are Logical (use the possibly-clamped display size)
                        Width = vm.DisplayWidth + padding.Left + padding.Right,
                        Height = vm.DisplayHeight + padding.Top + padding.Bottom
                    };
                    
                    // Auto-Run AI if requested
                    if (runAI)
                    {
                        // Use dispatcher to ensure window is shown/initialized before starting
                         Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                            vm.RemoveBackgroundCommand.Execute().Subscribe();
                         });
                    }

                    // Initial Interactive mode if requested
                    if (initialInteractive)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                            vm.IsPointRemovalMode = true;
                        });
                    }

                    // Save Action
                    vm.SaveAction = async () =>
                    {
                        try
                        {
                            var topLevel = TopLevel.GetTopLevel(win);
                            if (topLevel?.StorageProvider is { } storageProvider)
                            {
                                // Default the dialog to the format chosen in the pin's toolbar (PNG/JPG/WebP).
                                var (choices, defaultExt) = GimmeCapture.Views.Shared.VideoFilePickerTypes
                                    .SaveImageChoicesPreferring(vm.SelectedImageFormat);
                                var file = await storageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                                {
                                    Title = "Save Pinned Image",
                                    DefaultExtension = defaultExt,
                                    ShowOverwritePrompt = true,
                                    SuggestedFileName = $"{CaptureFileNameService.SuggestedBaseName(_viewModel.MainVm?.FileNameTemplate)}.{defaultExt}",
                                    FileTypeChoices = choices
                                });

                                if (file != null)
                                {
                                    // Encode to the ACTUALLY chosen extension (the dialog can override the toolbar
                                    // default) via SkiaSharp, since Avalonia's Bitmap.Save only writes PNG.
                                    string ext = System.IO.Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                                    var (skFormat, quality) = ext switch
                                    {
                                        "jpg" or "jpeg" => (SkiaSharp.SKEncodedImageFormat.Jpeg, 92),
                                        "webp" => (SkiaSharp.SKEncodedImageFormat.Webp, 90),
                                        _ => (SkiaSharp.SKEncodedImageFormat.Png, 100)
                                    };
                                    if (GimmeCapture.ViewModels.Floating.FloatingBitmapConversionHelper.TryEncodeBitmap(
                                            vm.Image, skFormat, quality, out var bytes, out var encErr))
                                    {
                                        using (var stream = new System.IO.FileStream(file.Path.LocalPath, System.IO.FileMode.Create))
                                        {
                                            await stream.WriteAsync(bytes, 0, bytes.Length);
                                        }
                                        FileLocationService.RevealInFileExplorer(file.Path.LocalPath);
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Failed to encode pinned image: {encErr}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("SnipWindow.SavePinnedImage", ex);
                        }
                    };
                    
                    win.Show();
                }
                catch (Exception ex)
                {
                    AppLog.Error("SnipWindow.ShowFloatingWindow", ex);
                }
            };
        }
    }
}
