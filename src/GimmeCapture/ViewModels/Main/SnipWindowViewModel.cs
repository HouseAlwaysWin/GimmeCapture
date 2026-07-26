using Avalonia;
using Avalonia.Media;
using System.Windows.Input;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.ComponentModel;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.OCR;
using GimmeCapture.ViewModels.Shared;
using System.Reactive.Disposables;
using Avalonia.Threading;

namespace GimmeCapture.ViewModels.Main;

public enum SnipState { Idle, Detecting, Selecting, Selected }
public enum SnipMode { Screenshot, Recording, Translation }
public enum SnipAutoAction { None, Copy, Pin, EnterRecordMode, TextCopy, ScrollingCapture }

public partial class SnipWindowViewModel : ViewModelBase, IDisposable, IDrawingToolViewModel
{
    /// <summary>
    /// Two-stage Esc decision for box-select mode: when a box has been drawn (finalized <c>Selected</c> state
    /// with a non-empty rect), the first Esc clears it and returns to the draw state rather than closing. Any
    /// other state (empty selection / auto-detect) means Esc closes the overlay. Pure/static so it is testable.
    /// </summary>
    internal static bool ShouldClearBoxToDraw(SnipState state, bool hasBox) =>
        state == SnipState.Selected && hasBox;

    private static readonly string[] _localizedPropertyNames =
    {
        nameof(SnipTooltip),
        nameof(RecordTooltip),
        nameof(TranslateTooltip),
        nameof(UndoTooltip),
        nameof(RedoTooltip),
        nameof(ClearTooltip),
        nameof(SaveTooltip),
        nameof(CopyTooltip),
        nameof(PinTooltip),
        nameof(RectangleTooltip),
        nameof(EllipseTooltip),
        nameof(ArrowTooltip),
        nameof(LineTooltip),
        nameof(PenTooltip),
        nameof(TextTooltip),
        nameof(CalloutTooltip),
        nameof(MosaicTooltip),
        nameof(BlurTooltip),
        nameof(HighlighterTooltip),
        nameof(StepTooltip),
        nameof(BringToFrontTooltip),
        nameof(SendToBackTooltip),
        nameof(FullscreenSelectTooltip),
        nameof(CaptureScopeTooltip),
        nameof(HideTranslationResultsTooltip),
        nameof(TranslationPinTooltip),
        nameof(TranslateAllTooltip),
        nameof(ScanAllTooltip),
        nameof(ClearAllTooltip),
        nameof(ToggleSelectTooltip),
        nameof(AutoDetectTooltip),
        nameof(ModeMultiTooltip),
        nameof(ToggleToolbarTooltip),
        nameof(InputAudioStateText),
        nameof(OutputAudioStateText)
    };

    private readonly RecordingService? _recordingService;
    public RecordingService? RecordingService => _recordingService;
    private readonly MainWindowViewModel? _mainVm;
    public MainWindowViewModel? MainVm => _mainVm;

    /// <summary>
    /// True when the capture overlay should open / stay WITHOUT stealing foreground focus (the
    /// CaptureWithoutStealingFocus setting is on, and we are not in Translation mode — translation needs real
    /// focus for its text controls / ComboBoxes). The view combines this with a temporary override while the
    /// annotation text editor is active. Drives ShowActivated + WS_EX_NOACTIVATE on the overlay.
    /// </summary>
    public bool ShouldAvoidStealingFocus =>
        _mainVm?.CaptureWithoutStealingFocus == true && CurrentMode != SnipMode.Translation && !IsFrozenFrameActive;

    // --- Freeze-frame capture: a still of the WHOLE desktop grabbed BEFORE the overlay was shown, so shell
    // "light dismiss" popups (tray flyout / Start menu / left-click dropdowns) — which close the instant any
    // full-screen overlay appears over them — are captured. The user selects/annotates on this still; commit
    // and OCR read from it instead of the live screen. Gated by the FreezeScreenOnScreenshot setting (factory).
    private SkiaSharp.SKBitmap? _frozenScreenSkBitmap;
    private double _frozenGrabScaling = 1.0;

    /// <summary>The frozen full-desktop still (physical pixels) used at commit/OCR time. Null when not frozen.</summary>
    public SkiaSharp.SKBitmap? FrozenScreenSkBitmap => _frozenScreenSkBitmap;
    /// <summary>The scaling used when the frozen still was grabbed; commit crops SelectionRect × this.</summary>
    public double FrozenGrabScaling => _frozenGrabScaling;

    private Avalonia.Media.Imaging.Bitmap? _frozenScreenSnapshot;
    /// <summary>Display-ready copy of the frozen still, bound to the full-window Image at the bottom of the overlay.</summary>
    public Avalonia.Media.Imaging.Bitmap? FrozenScreenSnapshot
    {
        get => _frozenScreenSnapshot;
        private set => this.RaiseAndSetIfChanged(ref _frozenScreenSnapshot, value);
    }

    private bool _isFrozenFrameActive;
    /// <summary>True when this overlay is showing a frozen desktop still (freeze-frame screenshot mode).</summary>
    public bool IsFrozenFrameActive
    {
        get => _isFrozenFrameActive;
        private set => this.RaiseAndSetIfChanged(ref _isFrozenFrameActive, value);
    }

    /// <summary>
    /// Stores the pre-overlay full-desktop grab (called by the factory BEFORE Show). Builds a display bitmap and
    /// activates freeze-frame mode. Ownership of <paramref name="frozen"/> transfers to this VM (disposed on close).
    /// </summary>
    public void SetFrozenScreenSnapshot(SkiaSharp.SKBitmap? frozen, double grabScaling)
    {
        _frozenScreenSkBitmap?.Dispose();
        _frozenScreenSkBitmap = frozen;
        _frozenGrabScaling = grabScaling > 0 ? grabScaling : 1.0;

        if (frozen != null
            && GimmeCapture.ViewModels.Floating.FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(frozen, out var display, out _)
            && display != null)
        {
            FrozenScreenSnapshot = display;
            IsFrozenFrameActive = true;
        }
        else
        {
            frozen?.Dispose();
            _frozenScreenSkBitmap = null;
            FrozenScreenSnapshot = null;
            IsFrozenFrameActive = false;
        }
    }

    /// <summary>
    /// Two-way access to the FreezeScreenOnScreenshot setting for the toolbar's freeze toggle, so the toolbar
    /// button and the settings checkbox are the same switch. It affects the NEXT capture (the current overlay's
    /// still is already grabbed) — same "persist a preference" behaviour the old lock-selection toolbar button had.
    /// </summary>
    public bool FreezeScreenOnScreenshot
    {
        get => _mainVm?.FreezeScreenOnScreenshot == true;
        set
        {
            if (_mainVm == null || _mainVm.FreezeScreenOnScreenshot == value) return;
            _mainVm.FreezeScreenOnScreenshot = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>The app-wide translation-overlay layer (null in design-time when there is no MainVm).</summary>
    public ITranslationResultLayerService? TranslationResultLayer => _mainVm?.TranslationResultLayer;
    private readonly IScreenCaptureService _captureService;
    private readonly ITranslationSessionService? _translationSession;
    private readonly ITranslationSelectionMonitor? _translationSelectionMonitor;
    private readonly IAIScanSessionService? _aiScanSessionService;
    private readonly IQuickOcrService? _quickOcrService;
    private readonly IOcrEngineFactory _ocrEngineFactory;
    private readonly ICaptureVisibilityCoordinator _captureVisibilityCoordinator;
    private readonly SnipSelectionStateController _selectionStateController;
    private readonly CompositeDisposable _disposables = new();
    private readonly AudioLevelMonitorService _audioLevelMonitor = new();
    private readonly DispatcherTimer _audioMeterTimer;
    internal bool IsAudioMeterTimerEnabledForTesting => _audioMeterTimer.IsEnabled;

    private SnipMode _currentMode = SnipMode.Screenshot;
    public SnipMode CurrentMode
    {
        get => _currentMode;
        set => SetCurrentMode(value);
    }

    // Hotkeys / Tooltips (Dynamic by Mode)
    public string SnipHotkey => _mainVm?.SnipHotkey ?? "Shift+F1";
    public string RecordHotkey => _mainVm?.RecordHotkey ?? "Shift+F2";
    public string TranslateHotkey => _mainVm?.TranslateHotkey ?? "Shift+F3";
    public string TextCopyHotkey => _mainVm?.TextCopyHotkey ?? "Shift+F4";
    
    public string CopyHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Copy ?? "Ctrl+C") : (_mainVm?.Snip_Copy ?? "Ctrl+C");
    public string UndoHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Undo ?? "Ctrl+Z") : (_mainVm?.Snip_Undo ?? "Ctrl+Z");
    public string RedoHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Redo ?? "Ctrl+Y") : (_mainVm?.Snip_Redo ?? "Ctrl+Y");
    // In translation mode, Delete is reserved for ClearAllHotkey.
    public string ClearHotkey => CurrentMode == SnipMode.Translation
        ? string.Empty
        : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Clear ?? "Delete") : (_mainVm?.Snip_Clear ?? "Delete"));
    public string SaveHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Save ?? "Ctrl+S") : (_mainVm?.Snip_Save ?? "Ctrl+S");
    public string CloseHotkey => CurrentMode == SnipMode.Translation ? (_mainVm?.Translate_Close ?? "Escape") : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Close ?? "Escape") : (_mainVm?.Snip_Close ?? "Escape"));
    
    // Annotation tool hotkeys are disabled in translation mode to avoid
    // stealing key gestures from translation-specific actions.
    public string RectangleHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Rectangle ?? "R") : (_mainVm?.Snip_Rectangle ?? "R"));
    public string EllipseHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Ellipse ?? "E") : (_mainVm?.Snip_Ellipse ?? "E"));
    public string ArrowHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Arrow ?? "A") : (_mainVm?.Snip_Arrow ?? "A"));
    public string LineHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Line ?? "L") : (_mainVm?.Snip_Line ?? "L"));
    public string PenHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Pen ?? "P") : (_mainVm?.Snip_Pen ?? "P"));
    public string TextHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Text ?? "T") : (_mainVm?.Snip_Text ?? "T"));
    public string MosaicHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Mosaic ?? "M") : (_mainVm?.Snip_Mosaic ?? "M"));
    public string BlurHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Blur ?? "B") : (_mainVm?.Snip_Blur ?? "B"));
    public string FullscreenSelectHotkey => CurrentMode switch
    {
        SnipMode.Recording => _mainVm?.Record_FullscreenSelect ?? "F",
        SnipMode.Screenshot => _mainVm?.Snip_FullscreenSelect ?? "F",
        _ => string.Empty
    };

    public string ActiveActionHotkey => CurrentMode == SnipMode.Translation ? (_mainVm?.Translate_Action ?? "F8") : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Action ?? "F6") : (_mainVm?.Snip_Pin ?? "F6"));
    public string ActiveToolbarHotkey => CurrentMode == SnipMode.Translation ? (_mainVm?.Translate_Toolbar ?? "F4") : (CurrentMode == SnipMode.Recording ? (_mainVm?.Record_Toolbar ?? "F4") : (_mainVm?.Snip_Toolbar ?? "F4"));
    public string ActivePlaybackHotkey => _mainVm?.Record_Playback ?? "Space";
    public string ActiveStopHotkey => _mainVm?.Record_Stop ?? "F9";
    public string RemoveBackgroundHotkey => _mainVm?.Snip_RemoveBackground ?? "Shift+R";
    public string MagicWandHotkey => _mainVm?.Snip_MagicWand ?? "W";
    public string SnipSelectionModeHotkey => _mainVm?.Snip_SelectionMode ?? "S";

    // Mode-switch hotkeys (resolved per current mode)
    // Return empty string when already in the target mode to avoid conflicts
    // so the current mode can reserve its own action key without duplicated bindings.
    public string SwitchToSnipHotkey => CurrentMode switch
    {
        SnipMode.Screenshot => string.Empty, // Already in Snip mode
        SnipMode.Recording => _mainVm?.Record_SwitchToSnip ?? "F1",
        SnipMode.Translation => _mainVm?.Translate_SwitchToSnip ?? "F1",
        _ => string.Empty
    };
    public string SwitchToRecordHotkey => CurrentMode switch
    {
        SnipMode.Recording => string.Empty, // Already in Record mode
        SnipMode.Screenshot => _mainVm?.Snip_SwitchToRecord ?? "F2",
        SnipMode.Translation => _mainVm?.Translate_SwitchToRecord ?? "F2",
        _ => string.Empty
    };
    public string SwitchToTranslateHotkey => CurrentMode switch
    {
        SnipMode.Translation => string.Empty, // Already in Translation mode
        SnipMode.Screenshot => _mainVm?.Snip_SwitchToTranslate ?? "F3",
        SnipMode.Recording => _mainVm?.Record_SwitchToTranslate ?? "F3",
        _ => string.Empty
    };

    // Translation mode specific hotkeys
    public string TranslateAllHotkey => _mainVm?.Translate_TranslateAll ?? "T";
    public string TranslatePinHotkey => _mainVm?.Translate_Pin ?? "F6";
    public string ScanAllHotkey => _mainVm?.Translate_ScanAll ?? "S";
    public string ClearAllHotkey => _mainVm?.Translate_ClearAll ?? "Delete";
    public string ToggleSelectHotkey => _mainVm?.Translate_ToggleSelect ?? "Tab";
    public string AutoDetectHotkey => _mainVm?.Translate_AutoDetect ?? "D";
    public string ModeCursorHotkey => _mainVm?.Translate_ModeCursor ?? "D1";
    public string ModeSingleHotkey => _mainVm?.Translate_ModeSingle ?? "D2";
    public string ModeMultiHotkey => _mainVm?.Translate_ModeMulti ?? "D3";
    /// <summary>Shift / Ctrl / Alt / None — matches settings.</summary>
    public string TranslationSelectionHoldModifier => _mainVm?.Translate_SelectionHoldModifier ?? "Ctrl";

    public string UndoTooltip => $"{LocalizationService.Instance["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{LocalizationService.Instance["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{LocalizationService.Instance["Clear"]} ({ClearHotkey})";
    public string SaveTooltip => $"{LocalizationService.Instance["TipSave"]} ({SaveHotkey})";
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? $"{LocalizationService.Instance["ActionStartPin"]} ({ActiveActionHotkey})" : $"{LocalizationService.Instance["TipPin"]} ({ActiveActionHotkey})");
    public string ScrollingCaptureTooltip => $"{LocalizationService.Instance["ScrollingCapture"]} ({_mainVm?.ScrollingCaptureHotkey ?? "Shift+F5"})";
    public string RectangleTooltip => $"{LocalizationService.Instance["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{LocalizationService.Instance["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{LocalizationService.Instance["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{LocalizationService.Instance["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{LocalizationService.Instance["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{LocalizationService.Instance["TipText"]} ({TextHotkey})";
    public string CalloutTooltip => LocalizationService.Instance["TipCallout"];
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";
    public string HighlighterTooltip => LocalizationService.Instance["TipHighlighter"];
    public string StepTooltip => LocalizationService.Instance["TipStep"];
    public string BringToFrontTooltip => LocalizationService.Instance["TipBringToFront"];
    public string SendToBackTooltip => LocalizationService.Instance["TipSendToBack"];
    public string FullscreenSelectTooltip => $"{LocalizationService.Instance["ActionSelectFullscreen"]} ({FullscreenSelectHotkey})";
    public string CaptureScopeTooltip => LocalizationService.Instance[SupportsWindowCapture ? "CaptureScopeTooltip" : "CaptureScopeTooltipMonitorOnly"];
    public string SnipTooltip => CurrentMode == SnipMode.Screenshot ? LocalizationService.Instance["CaptureModeNormal"] : $"{LocalizationService.Instance["CaptureModeNormal"]} ({SwitchToSnipHotkey})";
    public string RecordTooltip => CurrentMode == SnipMode.Recording ? LocalizationService.Instance["CaptureModeRecord"] : $"{LocalizationService.Instance["CaptureModeRecord"]} ({SwitchToRecordHotkey})";
    public string TranslateTooltip => CurrentMode == SnipMode.Translation ? (LocalizationService.Instance["CaptureModeTranslation"] ?? "Translation") : $"{(LocalizationService.Instance["CaptureModeTranslation"] ?? "Translation")} ({SwitchToTranslateHotkey})";
    public string HideTranslationResultsTooltip => $"{LocalizationService.Instance["HideTranslationResults"]} ({ActiveActionHotkey})";
    public string TranslationPinTooltip => $"{LocalizationService.Instance["MenuPinTranslation"]} ({TranslatePinHotkey})";
    public string TranslateAllTooltip => $"{LocalizationService.Instance["ActionTranslateAll"]} ({TranslateAllHotkey})";
    public string ScanAllTooltip => $"{LocalizationService.Instance["ActionScanAll"]} ({ScanAllHotkey})";
    public string ClearAllTooltip => $"{LocalizationService.Instance["ActionClearAll"]} ({ClearAllHotkey})";
    public string ToggleSelectTooltip => $"{LocalizationService.Instance["ActionToggleSelect"]} ({ToggleSelectHotkey})";
    public string AutoDetectTooltip => $"{LocalizationService.Instance["ActionAutoDetect"]} ({AutoDetectHotkey})";
    public string ModeCursorTooltip => $"{LocalizationService.Instance["TranslateModeCursor"]} ({ModeCursorHotkey})";
    public string ModeSingleTooltip => $"{LocalizationService.Instance["TranslateModeSingle"]} ({ModeSingleHotkey})";
    public string ModeMultiTooltip => $"{LocalizationService.Instance["TranslateModeMulti"]} ({ModeMultiHotkey})";
    public string ModeAudioTooltip => LocalizationService.Instance["TranslateModeAudio"] ?? "Audio Mode";
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({ActiveToolbarHotkey})";
    public string TogglePlaybackTooltip => $"{LocalizationService.Instance["ActionPlayback"]} ({ActivePlaybackHotkey})";
    public string RemoveBackgroundTooltip => $"{LocalizationService.Instance["RemoveBackground"]} ({RemoveBackgroundHotkey})";
    public string MagicWandTooltip => $"{LocalizationService.Instance["TipMagicWand"]} ({MagicWandHotkey})";
    
    public string MenuPinTranslation => LocalizationService.Instance["MenuPinTranslation"];
    public string MenuCopyTranslation => LocalizationService.Instance["CopyTranslation"] ?? "Copy Translation";

    public Color ThemeColor => _mainVm?.ThemeColor ?? Colors.Red;
    public Color OcrHighlightColor => _mainVm?.ThemeColor ?? SelectionBorderColor;
    public Color TranslationOcrHighlightColor => ResolveTranslationOcrHighlightColor(ThemeColor, SelectionBorderColor);
    public Color ThemeDeepColor 
    {
        get
        {
            if (ThemeColor == Color.Parse("#D4AF37")) return Color.Parse("#8B7500");
            if (ThemeColor == Color.Parse("#E0E0E0")) return Color.Parse("#606060");
            return Color.Parse("#900000");
        }
    }

    private static Color ResolveTranslationOcrHighlightColor(Color themeColor, Color selectionBorderColor)
    {
        ReadOnlySpan<Color> candidates =
        [
            Color.Parse("#00E5FF"),
            Color.Parse("#7CFF00"),
            Color.Parse("#FFC400"),
            Color.Parse("#FF4FD8"),
            Color.Parse("#FFFFFF")
        ];

        Color best = candidates[0];
        int bestScore = int.MinValue;

        foreach (Color candidate in candidates)
        {
            int score = Math.Min(
                ComputeColorDistance(candidate, themeColor),
                ComputeColorDistance(candidate, selectionBorderColor));

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static int ComputeColorDistance(Color left, Color right)
    {
        int dr = left.R - right.R;
        int dg = left.G - right.G;
        int db = left.B - right.B;
        return (dr * dr) + (dg * dg) + (db * db);
    }

    public bool IsAIDownloading => _mainVm?.AIResourceService.IsDownloading ?? false;
    public double AIResourceProgress => _mainVm?.AIResourceService.DownloadProgress ?? 0;

    // Actions
    public Action? CloseAction { get; set; }
    /// <summary>Assigned by SnipWindow: forcibly collapses the Win32 selection-border window region to a 1×1 stub
    /// and repaints, so the yellow selection ring can't linger on screen if the overlay isn't torn down.</summary>
    public Action? ForceClearSelectionRegionAction { get; set; }
    /// <summary>Assigned by SnipWindow: closes any leftover overlay windows (other SnipWindow instances and the
    /// scrolling-capture region/hint outlines) so no stale yellow selection frame survives after a recording.</summary>
    public Action? CloseStaleOverlayWindowsAction { get; set; }
    /// <summary>
    /// Assigned by SnipWindow: applies <see cref="GimmeCapture.Services.Interop.Win32Helpers.SetWindowDisplayAffinity"/> so FFmpeg/gdigrab can exclude chrome while keeping full SelectionRect size (Windows 10 2004+).
    /// </summary>
    public Action? SyncRecordingScreenCaptureAffinity { get; set; }
    public Action? HideAction { get; set; }
    public Action? ShowAction { get; set; }
    public Action? OpenRecordingProgressWindowAction { get; set; }
    public Action? CloseRecordingProgressWindowAction { get; set; }
    /// <summary>
    /// Assigned by SnipWindow: shows/closes a standalone always-on-top "processing" spinner. Used by flows that
    /// hide the snip overlay to grab a clean capture but then run a long background task (Quick-OCR text copy), so
    /// the in-overlay <see cref="ShowProcessingOverlay"/> — which lives on the now-hidden snip window — would not
    /// be visible. Without it, the multi-second OCR looks like the app has frozen.
    /// </summary>
    public Action? ShowProcessingWindowAction { get; set; }
    public Action? HideProcessingWindowAction { get; set; }
    public Action? SaveAction { get; set; }
    public Action? FocusWindowAction { get; set; }
    /// <summary>
    /// Shows an OK-only dialog owned by the snip overlay window itself. Assigned by SnipWindow so the
    /// dialog renders above the topmost overlay and the overlay's hit-test region is disabled while modal
    /// (otherwise the full-screen overlay swallows the clicks — see the "no Llama model" error case).
    /// </summary>
    public Func<string, string, Task>? ShowOkDialogAction { get; set; }
    internal Action? PersistTranslationSelectionsAction { get; set; }
    public Func<Task<Avalonia.Media.Imaging.WriteableBitmap?>>? CaptureDrawingModeSnapshotAsync { get; set; }
    // Runs the given screen-grab with the overlay briefly excluded from capture (WDA_EXCLUDEFROMCAPTURE) then
    // restored to WDA_NONE, so a full-screen OCR grab doesn't pick up the overlay's own chrome while the overlay
    // is NOT excluded continuously (which grays a Chromium window underneath — see ApplyRecordingScreenCaptureAffinity).
    // Wired by the View (needs the hwnd); null on non-Windows / design → callers fall back to the bare grab.
    public Func<Func<Task<SkiaSharp.SKBitmap>>, Task<SkiaSharp.SKBitmap>>? RunTranslationOcrGrabExcludedAsync { get; set; }
    // Last param: the scrollable content size (logical px) — non-null only for a long scrolling-capture pin,
    // where `rect` is the fixed viewport (original selection) and the full stitch scrolls inside it.
    public Action<Avalonia.Media.Imaging.Bitmap, Rect, Color, double, bool, bool, string?, double, Avalonia.Size?>? OpenPinWindowAction { get; set; }
    public Action<string, int, int, double, double, Color, double, bool, bool>? OpenPinnedVideoWindowAction { get; set; }
    public Func<Task<string?>>? PickSaveFileAction { get; set; }
    public IEnumerable<Color> PresetColors => PresetColorPalette.DefaultColors;

    public OCRLanguage SourceLanguage
    {
        get => _mainVm?.SourceLanguage ?? OCRLanguage.Auto;
        set { if (_mainVm != null) _mainVm.SourceLanguage = value; this.RaisePropertyChanged(); }
    }

    public TranslationLanguage TargetLanguage
    {
        get => _mainVm?.TargetLanguage ?? TranslationLanguage.TraditionalChinese;
        set { if (_mainVm != null) _mainVm.TargetLanguage = value; this.RaisePropertyChanged(); }
    }

    public ScrollingCaptureDirection ScrollingCaptureDirection
    {
        get => _mainVm?.ScrollingCaptureDirection ?? ScrollingCaptureDirection.Auto;
        set { if (_mainVm != null) _mainVm.ScrollingCaptureDirection = value; this.RaisePropertyChanged(); }
    }

    public List<OCRLanguage> AvailableOCRLanguages => _mainVm?.AvailableOCRLanguages ?? Enum.GetValues<OCRLanguage>().AsValueEnumerable().ToList();
    public List<TranslationLanguage> AvailableTranslationLanguages => _mainVm?.AvailableTranslationLanguages ?? Enum.GetValues<TranslationLanguage>().AsValueEnumerable().ToList();

    public SnipWindowViewModel() : this(Colors.Red, 2.0, CreateDesignScreenCaptureService(), CreateDesignWindowDetectionService(), null, null, null, null, null, null, null) { }

    public SnipWindowViewModel(Color borderColor, double borderThickness, RecordingService? recService = null, MainWindowViewModel? mainVm = null)
        : this(borderColor, borderThickness, CreateDesignScreenCaptureService(), CreateDesignWindowDetectionService(), recService, mainVm, null, null, null, null, null)
    {
    }

    public SnipWindowViewModel(
        Color borderColor,
        double borderThickness,
        IScreenCaptureService captureService,
        IWindowDetectionService? detectionService = null,
        RecordingService? recService = null,
        MainWindowViewModel? mainVm = null,
        ITranslationSessionService? translationSession = null,
        ITranslationSelectionMonitor? translationSelectionMonitor = null,
        IAIScanSessionService? aiScanSessionService = null,
        IQuickOcrService? quickOcrService = null,
        ICaptureVisibilityCoordinator? captureVisibilityCoordinator = null,
        IOcrEngineFactory? ocrEngineFactory = null)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _detectionService = detectionService ?? CreateDesignWindowDetectionService();
        _translationSession = translationSession;
        _translationSelectionMonitor = translationSelectionMonitor;
        _aiScanSessionService = aiScanSessionService;
        _quickOcrService = quickOcrService;
        _ocrEngineFactory = ocrEngineFactory ?? new PaddleOcrEngineFactory();
        _captureVisibilityCoordinator = captureVisibilityCoordinator ?? new ImmediateCaptureVisibilityCoordinator();
        _selectionBorderColor = borderColor;
        _selectionBorderThickness = borderThickness;
        _recordingService = recService;
        _mainVm = mainVm;
        _selectionStateController = new SnipSelectionStateController(
            shouldTriggerAutoScan: ShouldTriggerAutoScan,
            triggerAutoScan: () => RunOCRScanAsync().Forget("Snip.AutoScan"),
            cancelScan: () => _scanCts?.Cancel(),
            dismissHoverPreview: preserveTargetRect => DismissWindowSnapHoverPreview(preserveTargetRect),
            triggerAutoAction: TriggerAutoAction,
            clearTranslatedBlocks: () => TranslatedBlocks.Clear(),
            resetParkedToolbar: ResetParkedToolbarIfOffScreenWhenLeavingSelection,
            refreshInteractionRegion: RefreshInteractionRegion,
            hideScanLoadingBar: () =>
            {
                if (CurrentMode != SnipMode.Translation)
                {
                    ShowTopLoadingBar = false;
                }
            });
        _audioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        ApplyInitialMainVmVisualSettings();
        InitializeRecordingBindingsIfNeeded();

        _audioMeterTimer.Tick += (_, _) => RefreshAudioLevels();
        SyncAudioMeterTimerWithMode();

        InitializeActionCommands();
        InitializeToolbarCommands();
        InitializeSelectionCommands();
        InitializeCaptureScopeCommands();
        InitializeMainViewModelBindingsIfNeeded();
        _editorState.Changed.Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(CurrentAnnotationTool));
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsRedactionToolActive));
            this.RaisePropertyChanged(nameof(SelectedAnnotation));
            this.RaisePropertyChanged(nameof(StyleAnnotationTool));
            this.RaisePropertyChanged(nameof(SelectedColor));
            this.RaisePropertyChanged(nameof(CurrentThickness));
            this.RaisePropertyChanged(nameof(CurrentFontSize));
            this.RaisePropertyChanged(nameof(CurrentFontFamily));
            this.RaisePropertyChanged(nameof(IsBold));
            this.RaisePropertyChanged(nameof(IsItalic));
            this.RaisePropertyChanged(nameof(IsShapeFilled));
            this.RaisePropertyChanged(nameof(IsEnteringText));
            this.RaisePropertyChanged(nameof(PendingText));
            this.RaisePropertyChanged(nameof(TextInputPosition));
            this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
            UpdateHistoryStatus();
        }).DisposeWith(_disposables);

        // Initialize Debug Compatibility
        _isTopmost = !System.Diagnostics.Debugger.IsAttached;
        if (System.Diagnostics.Debugger.IsAttached)
        {
            AppLog.Information("SnipWindow.DebuggerDetected");
        }

        InitializeToolbarReactivity();
        InitializeLocalizationBindings();
        InitializeWindowSnapHoverAnimation();

    }

    private void ApplyInitialMainVmVisualSettings()
    {
        if (_mainVm == null)
        {
            return;
        }

        _selectionBorderColor = _mainVm.BorderColor;
        _selectionBorderThickness = _mainVm.BorderThickness;
    }

    private void ApplyDefaultToolbarVisibilityForMode(SnipMode mode)
    {
        if (_mainVm == null
            || mode == SnipMode.Translation
            || CurrentState != SnipState.Selected
            || IsRecordingFinalizing)
        {
            return;
        }

        bool shouldShowToolbar = mode == SnipMode.Recording
            ? !_mainVm.DefaultHideRecordToolbar
            : !_mainVm.DefaultHideSnipToolbar;

        ShowToolbar = shouldShowToolbar;
        if (!shouldShowToolbar)
        {
            ParkSnipToolbarOffscreen();
        }
    }

    private void InitializeRecordingBindingsIfNeeded()
    {
        if (_recordingService != null)
        {
            InitializeRecordingBindings(_recordingService);
        }
    }

    private void InitializeMainViewModelBindingsIfNeeded()
    {
        if (_mainVm != null)
        {
            InitializeMainViewModelBindings(_mainVm);
        }
    }

    private void InitializeToolbarReactivity()
    {
        // Keep the fixed top-center toolbar on whichever screen the cursor is on — re-center whenever the active
        // screen (or viewport / measured toolbar width) changes, so it follows the mouse across monitors. Applies
        // to every mode now (not just Translation); InitializeTranslationToolbarPosition respects a manual drag.
        this.WhenAnyValue(x => x.ViewportSize, x => x.ToolbarWidth, x => x.CurrentMode, x => x.ActiveScreenBounds)
            .DistinctUntilChanged()
            .Throttle(TimeSpan.FromMilliseconds(50), RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => InitializeTranslationToolbarPosition())
            .DisposeWith(_disposables);
    }

    private void InitializeLocalizationBindings()
    {
        // Refresh tooltips when language changes
        LocalizationService.Instance.PropertyChanged += OnLocalizationPropertyChanged;
    }

    private void InitializeRecordingBindings(RecordingService recordingService)
    {
        BindDistinct(
            recordingService.WhenAnyValue(x => x.State),
            _ =>
            {
                HandleRecordingStateChanged(recordingService.State);
                RaiseProperties(
                    nameof(RecState),
                    nameof(IsRecordingActive),
                    nameof(HideFrameBorder),
                    nameof(HideSelectionDecoration),
                    nameof(IsHoverPreviewVisible));
            },
            observeOnMainThread: true)
            .DisposeWith(_disposables);

        BindDistinct(
            recordingService.WhenAnyValue(x => x.IsFinalizing),
            isFinalizing =>
            {
                IsRecordingFinalizing = isFinalizing;
                if (isFinalizing)
                {
                    ProcessingText = LocalizationService.Instance["StatusProcessing"];
                    OpenRecordingProgressWindowAction?.Invoke();
                }
                else
                {
                    CloseRecordingProgressWindowAction?.Invoke();
                }
            },
            observeOnMainThread: true)
            .DisposeWith(_disposables);
    }

    private void InitializeMainViewModelBindings(MainWindowViewModel mainVm)
    {
        BindDistinct(mainVm.WhenAnyValue(x => x.ShowAIScanBox), val => ShowAIScanBox = val)
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.EnableAIScan), val => EnableAIScan = val)
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.ProgressValue), val => ProgressValue = val)
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.IsIndeterminate), val => IsIndeterminate = val)
            .DisposeWith(_disposables);

        // Sync ShowProcessingOverlay — only propagate 'true' from MainVM.
        // When MainVM goes false, only clear overlay if no local operation is active.
        BindDistinct(mainVm.WhenAnyValue(x => x.ShowProcessingOverlay), val =>
            {
                if (val)
                    ShowProcessingOverlay = true;
                else if (!_isLocalProcessing)
                    ShowProcessingOverlay = false;
            })
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.ThemeColor), _ =>
            RaiseProperties(nameof(ThemeColor), nameof(OcrHighlightColor), nameof(TranslationOcrHighlightColor), nameof(ThemeDeepColor)))
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.BorderColor), val =>
            {
                SelectionBorderColor = val;
                this.RaisePropertyChanged(nameof(OcrHighlightColor));
                this.RaisePropertyChanged(nameof(TranslationOcrHighlightColor));
            })
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.BorderThickness), val => SelectionBorderThickness = val)
            .DisposeWith(_disposables);

        // Real-time sync for decoration scales from MainVM
        BindDistinct(mainVm.WhenAnyValue(x => x.WingScale), _ =>
            {
                RaiseProperties(
                    nameof(WingScale),
                    nameof(WingWidth),
                    nameof(WingHeight),
                    nameof(LeftWingMargin),
                    nameof(RightWingMargin));
            })
            .DisposeWith(_disposables);

        BindDistinct(mainVm.WhenAnyValue(x => x.Translate_SelectionHoldModifier), _ =>
            this.RaisePropertyChanged(nameof(TranslationSelectionHoldModifier)))
            .DisposeWith(_disposables);
    }

    private IDisposable BindDistinct<T>(IObservable<T> source, Action<T> onNext, bool observeOnMainThread = false)
    {
        var stream = source.DistinctUntilChanged();
        if (observeOnMainThread)
        {
            stream = stream.ObserveOn(RxSchedulers.MainThreadScheduler);
        }

        return stream.Subscribe(onNext);
    }

    private void OnLocalizationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Item" || e.PropertyName == "Item[]")
        {
            RaiseProperties(_localizedPropertyNames);
        }
    }

    private void RaiseProperties(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            this.RaisePropertyChanged(propertyName);
        }
    }

    private double _inputAudioLevel;
    public double InputAudioLevel
    {
        get => _inputAudioLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _inputAudioLevel, value);
            this.RaisePropertyChanged(nameof(InputAudioDbText));
            this.RaisePropertyChanged(nameof(IsInputAudioActive));
            this.RaisePropertyChanged(nameof(InputAudioStateText));
        }
    }

    private double _outputAudioLevel;
    public double OutputAudioLevel
    {
        get => _outputAudioLevel;
        set
        {
            this.RaiseAndSetIfChanged(ref _outputAudioLevel, value);
            this.RaisePropertyChanged(nameof(OutputAudioDbText));
            this.RaisePropertyChanged(nameof(IsOutputAudioActive));
            this.RaisePropertyChanged(nameof(OutputAudioStateText));
        }
    }

    public string InputAudioDbText => $"{ToDecibel(InputAudioLevel):F1} dB";
    public string OutputAudioDbText => $"{ToDecibel(OutputAudioLevel):F1} dB";
    public bool IsInputAudioActive => InputAudioLevel >= 0.01;
    public bool IsOutputAudioActive => OutputAudioLevel >= 0.01;
    // Whether the recording captures the mic — drives the mic level meter's visibility in the toolbar.
    public bool IsMicrophoneEnabled => _mainVm?.RecordingSettings.RecordMicrophone ?? false;

    // Live mute toggles for the active recording (session-only; reset to unmuted on each Start). When muted
    // the capture writes silence so the audio timeline stays in sync — see RecordingService.Audio.cs.
    public bool IsSystemAudioMuted
    {
        get => _recordingService?.MuteSystemAudio ?? false;
        set
        {
            if (_recordingService != null && _recordingService.MuteSystemAudio != value)
            {
                _recordingService.MuteSystemAudio = value;
                this.RaisePropertyChanged();
            }
        }
    }

    public bool IsMicMuted
    {
        get => _recordingService?.MuteMicrophone ?? false;
        set
        {
            if (_recordingService != null && _recordingService.MuteMicrophone != value)
            {
                _recordingService.MuteMicrophone = value;
                this.RaisePropertyChanged();
            }
        }
    }
    public string InputAudioStateText => IsInputAudioActive
        ? (LocalizationService.Instance["RecordAudioActive"] ?? "Active")
        : (LocalizationService.Instance["RecordAudioIdle"] ?? "Idle");
    public string OutputAudioStateText => IsOutputAudioActive
        ? (LocalizationService.Instance["RecordAudioActive"] ?? "Active")
        : (LocalizationService.Instance["RecordAudioIdle"] ?? "Idle");

    private void RefreshAudioLevels()
    {
        if (CurrentMode != SnipMode.Recording)
        {
            InputAudioLevel = 0.0;
            OutputAudioLevel = 0.0;
            return;
        }

        // Meter the mic the recording will actually use (not just the system default input).
        _audioLevelMonitor.MicDeviceId = _mainVm?.RecordingSettings.SelectedMicDeviceId;
        if (_audioLevelMonitor.TryRefresh())
        {
            InputAudioLevel = QuantizeAudioLevel(_audioLevelMonitor.InputPeak);
            OutputAudioLevel = QuantizeAudioLevel(_audioLevelMonitor.OutputPeak);
            return;
        }

        InputAudioLevel = 0.0;
        OutputAudioLevel = 0.0;
    }

    private void SyncAudioMeterTimerWithMode()
    {
        if (CurrentMode == SnipMode.Recording)
        {
            if (!_audioMeterTimer.IsEnabled)
            {
                _audioMeterTimer.Start();
            }

            return;
        }

        if (_audioMeterTimer.IsEnabled)
        {
            _audioMeterTimer.Stop();
        }

        InputAudioLevel = 0.0;
        OutputAudioLevel = 0.0;
    }

    private static double QuantizeAudioLevel(double value)
    {
        // Quantize tiny jitter so we avoid high-frequency UI invalidations.
        return Math.Round(Math.Clamp(value, 0, 1), 2);
    }

    private static double ToDecibel(double linearPeak)
    {
        if (linearPeak <= 0.00001) return -60;
        return Math.Clamp(20 * Math.Log10(linearPeak), -60, 0);
    }

    private bool _showToolbar = true;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set 
        {
            if (_showToolbar == value) return;
            this.RaiseAndSetIfChanged(ref _showToolbar, value);
            if (CurrentMode == SnipMode.Translation
                || (CurrentState == SnipState.Selected && !IsRecordingFinalizing))
            {
                if (!value)
                    ParkSnipToolbarOffscreen();
                else
                    RestoreSnipToolbarFromOffscreenPark();
            }

            this.RaisePropertyChanged(nameof(IsToolbarVisible));
            this.RaisePropertyChanged(nameof(IsToolbarShownOnScreen));
        }
    }

    private bool _showTranslationResults = true;
    public bool ShowTranslationResults
    {
        get => _showTranslationResults;
        set => this.RaiseAndSetIfChanged(ref _showTranslationResults, value);
    }
    
    private bool _showOcrResult;
    public bool ShowOcrResult
    {
        get => _showOcrResult;
        set
        {
            if (_showOcrResult != value)
            {
                this.RaiseAndSetIfChanged(ref _showOcrResult, value);
                RefreshProjectedOcrRects();
            }
        }
    }

    public string ShowOcrResultTooltip => $"{LocalizationService.Instance["ToggleOcrResult"]} (Debug)";

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleTranslationResultsCommand { get; protected set; } = null!;

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set => this.RaiseAndSetIfChanged(ref _isCapturing, value);
    }

    private bool _isTopmost = true;
    public bool IsTopmost
    {
        get => _isTopmost;
        set => this.RaiseAndSetIfChanged(ref _isTopmost, value);
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    private string _processingText = string.Empty;
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

    private bool _isLocalProcessing;
    
    private bool _showProcessingOverlay;
    public bool ShowProcessingOverlay
    {
        get => _showProcessingOverlay;
        set => this.RaiseAndSetIfChanged(ref _showProcessingOverlay, value);
    }

    private bool _isInputFocused;
    public bool IsInputFocused
    {
        get => _isInputFocused;
        set => this.RaiseAndSetIfChanged(ref _isInputFocused, value);
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _quickOcrCts?.Cancel();
        _quickOcrCts?.Dispose();
        CancelTranslationWarmup();
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        LocalizationService.Instance.PropertyChanged -= OnLocalizationPropertyChanged;
        _audioMeterTimer.Stop();
        _audioLevelMonitor.Dispose();
        _hoverAnimationTimer.Stop();
        _disposables.Dispose();
        _translationSession?.Dispose();
        _aiScanSessionService?.Dispose();
        _recordTimer?.Stop();
        DisposeDrawingModeSnapshot();
        _frozenScreenSkBitmap?.Dispose();
        _frozenScreenSkBitmap = null;
        _frozenScreenSnapshot?.Dispose();
        _frozenScreenSnapshot = null;
        
        CloseAction = null;
        SyncRecordingScreenCaptureAffinity = null;
        HideAction = null;
        ShowAction = null;
        OpenRecordingProgressWindowAction = null;
        CloseRecordingProgressWindowAction = null;
        ShowProcessingWindowAction = null;
        HideProcessingWindowAction = null;
        FocusWindowAction = null;
        PersistTranslationSelectionsAction = null;
        CaptureDrawingModeSnapshotAsync = null;
        RunTranslationOcrGrabExcludedAsync = null;
        OpenPinnedVideoWindowAction = null;
        PickSaveFileAction = null;
    }
}
