using Avalonia;
using Avalonia.Media;
using System.Windows.Input;
using GimmeCapture.Models;
using ReactiveUI;
using ReactiveUI.Reactive;
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
using GimmeCapture.Services.Core.Rendering;
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

    // What the overlay tells the window that opened it, and the app services it uses; no services when it was opened
    // on its own (a test or the designer).
    private readonly ISnipWindowHost _host;
    private readonly SnipWindowServices? _services;

    // The settings it reads and the few its toolbar changes; none when opened on its own, which uses the defaults.
    private readonly ISnipOverlaySettings? _settings;

    // For the overlay's view, which also reports to the host, saves and pins with the services, and reads settings.
    public ISnipWindowHost Host => _host;
    public SnipWindowServices? Services => _services;
    public ISnipOverlaySettings? Settings => _settings;

    /// <summary>
    /// True when the capture overlay should open / stay WITHOUT stealing foreground focus (the
    /// CaptureWithoutStealingFocus setting is on, and we are not in Translation mode — translation needs real
    /// focus for its text controls / ComboBoxes). The view combines this with a temporary override while the
    /// annotation text editor is active. Drives ShowActivated + WS_EX_NOACTIVATE on the overlay.
    /// </summary>
    public bool ShouldAvoidStealingFocus =>
        _settings?.CaptureWithoutStealingFocus == true && CurrentMode != SnipMode.Translation
        && Surface.AllowsNoActivateOverlay;

    // --- Freeze-frame capture: a still of the WHOLE desktop grabbed BEFORE the overlay was shown, so shell
    // "light dismiss" popups (tray flyout / Start menu / left-click dropdowns) — which close the instant any
    // full-screen overlay appears over them — are captured. The user selects/annotates on this still; commit
    // and OCR read from it instead of the live screen. Gated by the FreezeScreenOnScreenshot setting (factory).
    //
    // The live-vs-frozen state and everything derived from it (which pixels a commit reads, whether hiding the
    // overlay is a no-op, the Win32 pass-through region, the backdrop, the focus policy) belong to OverlaySurface.
    // They used to be a boolean each consumer re-interpreted, which is how they drifted apart.
    private readonly OverlaySurface _surface;

    /// <summary>This overlay's pixel surface: the Live/Frozen state machine and the only way to commit a selection.</summary>
    public OverlaySurface Surface => _surface;

    /// <summary>The overlay geometry trio, sampled together because a grab is only correct if all three agree.</summary>
    internal OverlayGeometry Geometry => new(ViewportSize, ScreenOffset, VisualScaling);

    /// <summary>
    /// Everything a commit needs from this overlay, sampled fresh per call. Supplying it through the surface is
    /// what keeps <c>Surface.CommitAsync()</c> argument-free at the five call sites.
    /// </summary>
    private SelectionCommit BuildSelectionCommit() => SelectionCommit.Annotated(
        SelectionRect,
        Geometry,
        Annotations,
        GetTranslationSelectionsForCapture(),
        TranslatedBlocks,
        _settings?.ShowSnipCursor ?? false);

    /// <summary>Maps the overlay's mode plus the manual-scroll flag onto the surface's activity gate.</summary>
    private OverlayActivity CurrentOverlayActivity => _manualScrollActive
        ? OverlayActivity.ManualScrolling
        : CurrentMode switch
        {
            SnipMode.Recording => OverlayActivity.Recording,
            SnipMode.Translation => OverlayActivity.Translation,
            _ => OverlayActivity.Screenshot
        };

    /// <summary>Re-applies the activity gate after a mode or manual-scroll transition, so a frozen still can
    /// never survive into an overlay whose mode is inherently live.</summary>
    internal void SyncSurfaceActivity() => _surface.ConstrainTo(CurrentOverlayActivity);

    /// <summary>The app-wide translation-overlay layer (null for an overlay opened without services).</summary>
    public ITranslationResultLayerService? TranslationResultLayer => _services?.TranslationResultLayer;
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

    // The overlay's own hotkeys, read live from the settings model: the main window's Snip_*, Record_* and Translate_*
    // settings read and write these same objects. Null for an overlay opened on its own, which uses the fallbacks.
    private SnipHotkeys? SnipKeys => _services?.SettingsService.Settings.Snip;
    private RecordHotkeys? RecordKeys => _services?.SettingsService.Settings.Record;
    private TranslateHotkeys? TranslateKeys => _services?.SettingsService.Settings.Translate;

    // Hotkeys / Tooltips (Dynamic by Mode)
    public string SnipHotkey => _settings?.SnipHotkey ?? "Shift+F1";
    public string RecordHotkey => _settings?.RecordHotkey ?? "Shift+F2";
    public string TranslateHotkey => _settings?.TranslateHotkey ?? "Shift+F3";
    public string TextCopyHotkey => _settings?.TextCopyHotkey ?? "Shift+F4";
    
    public string CopyHotkey => CurrentMode == SnipMode.Recording ? (RecordKeys?.Copy ?? "Ctrl+C") : (SnipKeys?.Copy ?? "Ctrl+C");
    public string UndoHotkey => CurrentMode == SnipMode.Recording ? (RecordKeys?.Undo ?? "Ctrl+Z") : (SnipKeys?.Undo ?? "Ctrl+Z");
    public string RedoHotkey => CurrentMode == SnipMode.Recording ? (RecordKeys?.Redo ?? "Ctrl+Y") : (SnipKeys?.Redo ?? "Ctrl+Y");
    // In translation mode, Delete is reserved for ClearAllHotkey.
    public string ClearHotkey => CurrentMode == SnipMode.Translation
        ? string.Empty
        : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Clear ?? "Delete") : (SnipKeys?.Clear ?? "Delete"));
    public string SaveHotkey => CurrentMode == SnipMode.Recording ? (RecordKeys?.Save ?? "Ctrl+S") : (SnipKeys?.Save ?? "Ctrl+S");
    public string CloseHotkey => CurrentMode == SnipMode.Translation ? (TranslateKeys?.Close ?? "Escape") : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Close ?? "Escape") : (SnipKeys?.Close ?? "Escape"));
    
    // Annotation tool hotkeys are disabled in translation mode to avoid
    // stealing key gestures from translation-specific actions.
    public string RectangleHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Rectangle ?? "R") : (SnipKeys?.Rectangle ?? "R"));
    public string EllipseHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Ellipse ?? "E") : (SnipKeys?.Ellipse ?? "E"));
    public string ArrowHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Arrow ?? "A") : (SnipKeys?.Arrow ?? "A"));
    public string LineHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Line ?? "L") : (SnipKeys?.Line ?? "L"));
    public string PenHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Pen ?? "P") : (SnipKeys?.Pen ?? "P"));
    public string TextHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Text ?? "T") : (SnipKeys?.Text ?? "T"));
    public string MosaicHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Mosaic ?? "M") : (SnipKeys?.Mosaic ?? "M"));
    public string BlurHotkey => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Blur ?? "B") : (SnipKeys?.Blur ?? "B"));
    public string FullscreenSelectHotkey => CurrentMode switch
    {
        SnipMode.Recording => RecordKeys?.FullscreenSelect ?? "F",
        SnipMode.Screenshot => SnipKeys?.FullscreenSelect ?? "F",
        _ => string.Empty
    };

    public string ActiveActionHotkey => CurrentMode == SnipMode.Translation ? (TranslateKeys?.Action ?? "F8") : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Action ?? "F6") : (SnipKeys?.Pin ?? "F6"));
    public string ActiveToolbarHotkey => CurrentMode == SnipMode.Translation ? (TranslateKeys?.Toolbar ?? "F4") : (CurrentMode == SnipMode.Recording ? (RecordKeys?.Toolbar ?? "F4") : (SnipKeys?.Toolbar ?? "F4"));
    public string ActivePlaybackHotkey => RecordKeys?.Playback ?? "Space";
    public string ActiveStopHotkey => RecordKeys?.Stop ?? "F9";
    public string RemoveBackgroundHotkey => SnipKeys?.RemoveBackground ?? "Shift+R";
    public string MagicWandHotkey => SnipKeys?.MagicWand ?? "W";
    public string SnipSelectionModeHotkey => SnipKeys?.SelectionMode ?? "S";

    // Mode-switch hotkeys (resolved per current mode)
    // Return empty string when already in the target mode to avoid conflicts
    // so the current mode can reserve its own action key without duplicated bindings.
    public string SwitchToSnipHotkey => CurrentMode switch
    {
        SnipMode.Screenshot => string.Empty, // Already in Snip mode
        SnipMode.Recording => RecordKeys?.SwitchToSnip ?? "F1",
        SnipMode.Translation => TranslateKeys?.SwitchToSnip ?? "F1",
        _ => string.Empty
    };
    public string SwitchToRecordHotkey => CurrentMode switch
    {
        SnipMode.Recording => string.Empty, // Already in Record mode
        SnipMode.Screenshot => SnipKeys?.SwitchToRecord ?? "F2",
        SnipMode.Translation => TranslateKeys?.SwitchToRecord ?? "F2",
        _ => string.Empty
    };
    public string SwitchToTranslateHotkey => CurrentMode switch
    {
        SnipMode.Translation => string.Empty, // Already in Translation mode
        SnipMode.Screenshot => SnipKeys?.SwitchToTranslate ?? "F3",
        SnipMode.Recording => RecordKeys?.SwitchToTranslate ?? "F3",
        _ => string.Empty
    };

    // Translation mode specific hotkeys
    public string TranslateAllHotkey => TranslateKeys?.TranslateAll ?? "T";
    public string TranslatePinHotkey => TranslateKeys?.Pin ?? "F6";
    public string ScanAllHotkey => TranslateKeys?.ScanAll ?? "S";
    public string ClearAllHotkey => TranslateKeys?.ClearAll ?? "Delete";
    public string ToggleSelectHotkey => TranslateKeys?.ToggleSelect ?? "Tab";
    public string AutoDetectHotkey => TranslateKeys?.AutoDetect ?? "D";
    public string ModeCursorHotkey => TranslateKeys?.ModeCursor ?? "D1";
    public string ModeSingleHotkey => TranslateKeys?.ModeSingle ?? "D2";
    public string ModeMultiHotkey => TranslateKeys?.ModeMulti ?? "D3";
    /// <summary>Shift / Ctrl / Alt / None — matches settings.</summary>
    public string TranslationSelectionHoldModifier => TranslateKeys?.SelectionHoldModifier ?? "Ctrl";

    public string UndoTooltip => $"{Localization["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{Localization["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{Localization["Clear"]} ({ClearHotkey})";
    public string SaveTooltip => $"{Localization["TipSave"]} ({SaveHotkey})";
    public string CopyTooltip => $"{Localization["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? $"{Localization["ActionStartPin"]} ({ActiveActionHotkey})" : $"{Localization["TipPin"]} ({ActiveActionHotkey})");
    public string ScrollingCaptureTooltip => $"{Localization["ScrollingCapture"]} ({_settings?.ScrollingCaptureHotkey ?? "Shift+F5"})";
    public string RectangleTooltip => $"{Localization["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{Localization["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{Localization["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{Localization["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{Localization["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{Localization["TipText"]} ({TextHotkey})";
    public string CalloutTooltip => Localization["TipCallout"];
    public string MosaicTooltip => $"{Localization["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{Localization["TipBlur"]} ({BlurHotkey})";
    public string HighlighterTooltip => Localization["TipHighlighter"];
    public string StepTooltip => Localization["TipStep"];
    public string BringToFrontTooltip => Localization["TipBringToFront"];
    public string SendToBackTooltip => Localization["TipSendToBack"];
    public string FullscreenSelectTooltip => $"{Localization["ActionSelectFullscreen"]} ({FullscreenSelectHotkey})";
    public string CaptureScopeTooltip => Localization[SupportsWindowCapture ? "CaptureScopeTooltip" : "CaptureScopeTooltipMonitorOnly"];
    public string SnipTooltip => CurrentMode == SnipMode.Screenshot ? Localization["CaptureModeNormal"] : $"{Localization["CaptureModeNormal"]} ({SwitchToSnipHotkey})";
    public string RecordTooltip => CurrentMode == SnipMode.Recording ? Localization["CaptureModeRecord"] : $"{Localization["CaptureModeRecord"]} ({SwitchToRecordHotkey})";
    public string TranslateTooltip => CurrentMode == SnipMode.Translation ? (Localization["CaptureModeTranslation"] ?? "Translation") : $"{(Localization["CaptureModeTranslation"] ?? "Translation")} ({SwitchToTranslateHotkey})";
    public string HideTranslationResultsTooltip => $"{Localization["HideTranslationResults"]} ({ActiveActionHotkey})";
    public string TranslationPinTooltip => $"{Localization["MenuPinTranslation"]} ({TranslatePinHotkey})";
    public string TranslateAllTooltip => $"{Localization["ActionTranslateAll"]} ({TranslateAllHotkey})";
    public string ScanAllTooltip => $"{Localization["ActionScanAll"]} ({ScanAllHotkey})";
    public string ClearAllTooltip => $"{Localization["ActionClearAll"]} ({ClearAllHotkey})";
    public string ToggleSelectTooltip => $"{Localization["ActionToggleSelect"]} ({ToggleSelectHotkey})";
    public string AutoDetectTooltip => $"{Localization["ActionAutoDetect"]} ({AutoDetectHotkey})";
    public string ModeCursorTooltip => $"{Localization["TranslateModeCursor"]} ({ModeCursorHotkey})";
    public string ModeSingleTooltip => $"{Localization["TranslateModeSingle"]} ({ModeSingleHotkey})";
    public string ModeMultiTooltip => $"{Localization["TranslateModeMulti"]} ({ModeMultiHotkey})";
    public string ModeAudioTooltip => Localization["TranslateModeAudio"] ?? "Audio Mode";
    public string ToggleToolbarTooltip => $"{Localization["ActionToolbar"]} ({ActiveToolbarHotkey})";
    public string TogglePlaybackTooltip => $"{Localization["ActionPlayback"]} ({ActivePlaybackHotkey})";
    public string RemoveBackgroundTooltip => $"{Localization["RemoveBackground"]} ({RemoveBackgroundHotkey})";
    public string MagicWandTooltip => $"{Localization["TipMagicWand"]} ({MagicWandHotkey})";
    
    public string MenuPinTranslation => Localization["MenuPinTranslation"];
    public string MenuCopyTranslation => Localization["CopyTranslation"] ?? "Copy Translation";

    public Color ThemeColor => _settings?.ThemeColor ?? Colors.Red;
    public Color OcrHighlightColor => _settings?.ThemeColor ?? SelectionBorderColor;
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
    private Action? _hideAction;
    /// <summary>Hides the overlay window (View-wired). Forwarded to the surface, which owns the decision of
    /// whether hiding is needed at all — it is a no-op when frozen.</summary>
    public Action? HideAction
    {
        get => _hideAction;
        set
        {
            _hideAction = value;
            _surface.HideOverlay = value;
        }
    }
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
    private Func<Func<Task<SkiaSharp.SKBitmap>>, Task<SkiaSharp.SKBitmap>>? _runTranslationOcrGrabExcludedAsync;
    public Func<Func<Task<SkiaSharp.SKBitmap>>, Task<SkiaSharp.SKBitmap>>? RunTranslationOcrGrabExcludedAsync
    {
        get => _runTranslationOcrGrabExcludedAsync;
        set
        {
            _runTranslationOcrGrabExcludedAsync = value;
            _surface.RunGrabExcludingOverlay = value;
        }
    }
    // Last param: the scrollable content size (logical px) — non-null only for a long scrolling-capture pin,
    // where `rect` is the fixed viewport (original selection) and the full stitch scrolls inside it.
    public Action<Avalonia.Media.Imaging.Bitmap, Rect, Color, double, bool, bool, string?, double, Avalonia.Size?>? OpenPinWindowAction { get; set; }
    public Action<string, int, int, double, double, Color, double, bool, bool>? OpenPinnedVideoWindowAction { get; set; }
    public Func<Task<string?>>? PickSaveFileAction { get; set; }
    public IEnumerable<Color> PresetColors => PresetColorPalette.DefaultColors;

    public OCRLanguage SourceLanguage
    {
        get => _settings?.SourceLanguage ?? OCRLanguage.Auto;
        set { if (_settings != null) _settings.SourceLanguage = value; this.RaisePropertyChanged(); }
    }

    public TranslationLanguage TargetLanguage
    {
        get => _settings?.TargetLanguage ?? TranslationLanguage.TraditionalChinese;
        set { if (_settings != null) _settings.TargetLanguage = value; this.RaisePropertyChanged(); }
    }

    public ScrollingCaptureDirection ScrollingCaptureDirection
    {
        get => _settings?.ScrollingCaptureDirection ?? ScrollingCaptureDirection.Auto;
        set { if (_settings != null) _settings.ScrollingCaptureDirection = value; this.RaisePropertyChanged(); }
    }

    public List<OCRLanguage> AvailableOCRLanguages => _settings?.AvailableOCRLanguages ?? Enum.GetValues<OCRLanguage>().AsValueEnumerable().ToList();
    public List<TranslationLanguage> AvailableTranslationLanguages => _settings?.AvailableTranslationLanguages ?? Enum.GetValues<TranslationLanguage>().AsValueEnumerable().ToList();

    public SnipWindowViewModel() : this(Colors.Red, 2.0, CreateDesignScreenCaptureService(), CreateDesignWindowDetectionService()) { }

    public SnipWindowViewModel(
        Color borderColor,
        double borderThickness,
        IScreenCaptureService captureService,
        IWindowDetectionService? detectionService = null,
        RecordingService? recService = null,
        ITranslationSessionService? translationSession = null,
        ITranslationSelectionMonitor? translationSelectionMonitor = null,
        IAIScanSessionService? aiScanSessionService = null,
        IQuickOcrService? quickOcrService = null,
        ICaptureVisibilityCoordinator? captureVisibilityCoordinator = null,
        IOcrEngineFactory? ocrEngineFactory = null,
        ISnipWindowHost? host = null,
        SnipWindowServices? services = null,
        ISnipOverlaySettings? settings = null,
        ILocalizationService? localization = null)
        : base(localization)
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
        _host = host ?? NullSnipWindowHost.Instance;
        _services = services;
        _settings = settings;
        _surface = new OverlaySurface(_captureService, _captureVisibilityCoordinator, BuildSelectionCommit);
        // ShouldAvoidStealingFocus derives from the surface, so the View's WS_EX_NOACTIVATE re-apply stream has to
        // see freeze transitions too — before, a mid-session freeze flipped the value and never re-applied the style.
        _surface.WhenAnyValue(x => x.IsFrozen)
            .Skip(1)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ShouldAvoidStealingFocus)))
            .DisposeWith(_disposables);
        // The overlay's unfocused capture keys are served by a process-wide keyboard hook that an elevated
        // foreground window silences, so they are mirrored as temporary global hotkeys whenever the overlay is
        // not holding focus. Mode/translation/text-entry/freeze all change either that state or which keys apply.
        Observable.Merge(
                this.WhenAnyValue(x => x.CurrentMode).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.IsTranslationMode).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.IsEnteringText).Select(_ => Unit.Default),
                _surface.WhenAnyValue(x => x.IsFrozen).Select(_ => Unit.Default))
            .Subscribe(_ => SyncUnfocusedOverlayHotkeys())
            .DisposeWith(_disposables);
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

        ApplyInitialVisualSettings();
        InitializeRecordingBindingsIfNeeded();

        _audioMeterTimer.Tick += (_, _) => RefreshAudioLevels();
        SyncAudioMeterTimerWithMode();

        InitializeActionCommands();
        InitializeToolbarCommands();
        InitializeSelectionCommands();
        InitializeCaptureScopeCommands();
        InitializeHostBindings();
        InitializeSettingsBindingsIfNeeded();
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

    private void ApplyInitialVisualSettings()
    {
        if (_settings == null)
        {
            return;
        }

        _selectionBorderColor = _settings.BorderColor;
        _selectionBorderThickness = _settings.BorderThickness;
    }

    private void ApplyDefaultToolbarVisibilityForMode(SnipMode mode)
    {
        if (_settings == null
            || mode == SnipMode.Translation
            || CurrentState != SnipState.Selected
            || IsRecordingFinalizing)
        {
            return;
        }

        bool shouldShowToolbar = mode == SnipMode.Recording
            ? !_settings.DefaultHideRecordToolbar
            : !_settings.DefaultHideSnipToolbar;

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

    private void InitializeSettingsBindingsIfNeeded()
    {
        if (_settings != null)
        {
            InitializeSettingsBindings(_settings);
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
        Localization.PropertyChanged += OnLocalizationPropertyChanged;
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
                    ProcessingText = Localization["StatusProcessing"];
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

    // The main window's progress, mirrored while the overlay is open. A NullSnipWindowHost reports the overlay's own
    // defaults (nothing in progress), so binding to it changes nothing.
    private void InitializeHostBindings()
    {
        BindDistinct(_host.WhenAnyValue(x => x.ProgressValue), val => ProgressValue = val)
            .DisposeWith(_disposables);

        BindDistinct(_host.WhenAnyValue(x => x.IsIndeterminate), val => IsIndeterminate = val)
            .DisposeWith(_disposables);

        // Sync ShowProcessingOverlay — only propagate 'true' from the host.
        // When the host goes false, only clear overlay if no local operation is active.
        BindDistinct(_host.WhenAnyValue(x => x.ShowProcessingOverlay), val =>
            {
                if (val)
                    ShowProcessingOverlay = true;
                else if (!_isLocalProcessing)
                    ShowProcessingOverlay = false;
            })
            .DisposeWith(_disposables);
    }

    private void InitializeSettingsBindings(ISnipOverlaySettings settings)
    {
        BindDistinct(settings.WhenAnyValue(x => x.ShowAIScanBox), val => ShowAIScanBox = val)
            .DisposeWith(_disposables);

        BindDistinct(settings.WhenAnyValue(x => x.EnableAIScan), val => EnableAIScan = val)
            .DisposeWith(_disposables);

        BindDistinct(settings.WhenAnyValue(x => x.ThemeColor), _ =>
            RaiseProperties(nameof(ThemeColor), nameof(OcrHighlightColor), nameof(TranslationOcrHighlightColor), nameof(ThemeDeepColor)))
            .DisposeWith(_disposables);

        BindDistinct(settings.WhenAnyValue(x => x.BorderColor), val =>
            {
                SelectionBorderColor = val;
                this.RaisePropertyChanged(nameof(OcrHighlightColor));
                this.RaisePropertyChanged(nameof(TranslationOcrHighlightColor));
            })
            .DisposeWith(_disposables);

        BindDistinct(settings.WhenAnyValue(x => x.BorderThickness), val => SelectionBorderThickness = val)
            .DisposeWith(_disposables);

        // Real-time sync for decoration scales from the settings
        BindDistinct(settings.WhenAnyValue(x => x.WingScale), _ =>
            {
                RaiseProperties(
                    nameof(WingScale),
                    nameof(WingWidth),
                    nameof(WingHeight),
                    nameof(LeftWingMargin),
                    nameof(RightWingMargin));
            })
            .DisposeWith(_disposables);

        BindDistinct(settings.WhenAnyValue(x => x.Translate_SelectionHoldModifier), _ =>
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
    public bool IsMicrophoneEnabled => _settings?.RecordingSettings.RecordMicrophone ?? false;

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
        ? (Localization["RecordAudioActive"] ?? "Active")
        : (Localization["RecordAudioIdle"] ?? "Idle");
    public string OutputAudioStateText => IsOutputAudioActive
        ? (Localization["RecordAudioActive"] ?? "Active")
        : (Localization["RecordAudioIdle"] ?? "Idle");

    private void RefreshAudioLevels()
    {
        if (CurrentMode != SnipMode.Recording)
        {
            InputAudioLevel = 0.0;
            OutputAudioLevel = 0.0;
            return;
        }

        // Meter the mic the recording will actually use (not just the system default input).
        _audioLevelMonitor.MicDeviceId = _settings?.RecordingSettings.SelectedMicDeviceId;
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

    public string ShowOcrResultTooltip => $"{Localization["ToggleOcrResult"]} (Debug)";

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
        // Only a mode switch used to stop this loop, and closing the overlay is not a mode switch: a translate
        // overlay closed with auto-detect on kept capturing the screen, running OCR and translating every 1.5 s for
        // the rest of the session — holding this disposed view model alive the whole time.
        StopAutoDetectLoop();
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        Localization.PropertyChanged -= OnLocalizationPropertyChanged;
        _audioMeterTimer.Stop();
        _audioLevelMonitor.Dispose();
        _hoverAnimationTimer.Stop();
        UnregisterUnfocusedOverlayHotkeys();
        _disposables.Dispose();
        _translationSession?.Dispose();
        _aiScanSessionService?.Dispose();
        _recordTimer?.Stop();
        DisposeDrawingModeSnapshot();
        _surface.Dispose();

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
