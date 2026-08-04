using GimmeCapture.Services.Core;
using System.Text.Json.Serialization;

namespace GimmeCapture.Models;

// Av1 is appended last so existing by-name/by-value JSON (H264=0, H265=1) still deserializes. It is offered
// only in the offline Compress pipeline (SVT-AV1); realtime recording never uses it.
public enum VideoCodec { H264, H265, Av1 }
public enum VideoQuality { Low, Medium, High }
// Whether to try GPU/Media-Foundation encoders (NVENC/QSV/AMF) before software libx264/265.
public enum VideoEncoderHint { PreferHardware, SoftwareOnly }
public enum TranslationLanguage { TraditionalChinese, SimplifiedChinese, English, Japanese, Korean }
public enum OCRLanguage { Auto, English, TraditionalChinese, SimplifiedChinese, Japanese, Korean }
public enum TranslationEngine { LlamaSharp }
public enum AIScanEngine { OCR, SAM2 }
public enum CaptureDelay { Off = 0, OneSecond = 1, ThreeSeconds = 3, FiveSeconds = 5, TenSeconds = 10 }
public enum OcrTextLayout { PreserveLines, SingleLine }
public enum ScrollingCaptureDirection { Auto, Vertical, Horizontal }
// Where the snip region-selection toolbar is pinned along the top of the active screen.
public enum SnipToolbarPosition { TopLeft, TopCenter, TopRight }
// What to do with the clip when the auto-stop recording length is reached.
public enum RecordingAutoStopAction { Pin, Save }

public class TranslatedBlock
{
    public string OriginalText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public Avalonia.Rect Bounds { get; set; }
    public double InferredFontSize { get; set; } = 12.0;
    public double DisplayFontSize { get; set; } = 12.0;
    public bool IsTextOverflowing { get; set; }
}

public class SnipHotkeys
{
    public string Rectangle { get; set; } = "R";
    public string Ellipse { get; set; } = "E";
    public string Arrow { get; set; } = "A";
    public string Line { get; set; } = "L";
    public string Pen { get; set; } = "P";
    public string Text { get; set; } = "T";
    public string Mosaic { get; set; } = "M";
    public string Blur { get; set; } = "B";
    public string Undo { get; set; } = "Ctrl+Z";
    public string Redo { get; set; } = "Ctrl+Y";
    public string Clear { get; set; } = "Delete";
    public string Save { get; set; } = "Ctrl+S";
    public string Copy { get; set; } = "Ctrl+C";
    public string Pin { get; set; } = "F6";
    public string Close { get; set; } = "Escape";
    public string Toolbar { get; set; } = "F4";
    public string SelectionMode { get; set; } = "S";
    public string CropMode { get; set; } = "C";
    public string RemoveBackground { get; set; } = "Shift+R";
    public string MagicWand { get; set; } = "W";
    public string FullscreenSelect { get; set; } = "F";
    public string SwitchToTranslate { get; set; } = "F3";
    public string SwitchToRecord { get; set; } = "F2";
}

public class RecordHotkeys
{
    public string Rectangle { get; set; } = "R";
    public string Ellipse { get; set; } = "E";
    public string Arrow { get; set; } = "A";
    public string Line { get; set; } = "L";
    public string Pen { get; set; } = "P";
    public string Text { get; set; } = "T";
    public string Mosaic { get; set; } = "M";
    public string Blur { get; set; } = "B";
    public string Undo { get; set; } = "Ctrl+Z";
    public string Redo { get; set; } = "Ctrl+Y";
    public string Clear { get; set; } = "Delete";
    public string Save { get; set; } = "Ctrl+S";
    public string Copy { get; set; } = "Ctrl+C";
    public string Close { get; set; } = "Escape";
    public string Toolbar { get; set; } = "F4";
    public string Action { get; set; } = "F6";
    public string Playback { get; set; } = "Space";
    public string Stop { get; set; } = "F9";
    public string FullscreenSelect { get; set; } = "F";
    public string SwitchToSnip { get; set; } = "F1";
    public string SwitchToTranslate { get; set; } = "F3";
}

public class TranslateHotkeys
{
    public string Action { get; set; } = "F8";
    public string Pin { get; set; } = "F6";
    public string Toolbar { get; set; } = "F4";
    public string Close { get; set; } = "Escape";
    public string TranslateAll { get; set; } = "T";
    public string ScanAll { get; set; } = "S";
    public string ClearAll { get; set; } = "Delete";
    public string ToggleSelect { get; set; } = "Tab";
    public string AutoDetect { get; set; } = "D";
    /// <summary>Shift, Ctrl, Alt, or None — hold while dragging to box-select in translation mode.</summary>
    public string SelectionHoldModifier { get; set; } = "Ctrl";
    public string ModeCursor { get; set; } = "D1";
    public string ModeSingle { get; set; } = "D2";
    public string ModeMulti { get; set; } = "D3";
    public string SwitchToSnip { get; set; } = "F1";
    public string SwitchToRecord { get; set; } = "F2";
}

public class AppSettings
{
    public int ConfigVersion { get; set; } = AppSettingsService.CurrentConfigVersion;
    public Language Language { get; set; } = Language.English;
    public bool RunOnStartup { get; set; }
    public bool AutoCheckUpdates { get; set; }
    
    // Snip
    public double BorderThickness { get; set; } = 2.0;
    public string BorderColorHex { get; set; } = "#E60012";
    public string ThemeColorHex { get; set; } = "#E60012";
    public double WingScale { get; set; } = 1.0;
    // Visibility
    public bool HideSnipPinDecoration { get; set; } = false;
    public bool HideSnipPinBorder { get; set; } = false;
    public bool HideSnipSelectionDecoration { get; set; } = false;
    /// <summary>
    /// When true, the screenshot/record overlay opens WITHOUT stealing foreground focus (ShowActivated=false +
    /// WS_EX_NOACTIVATE), so focus-sensitive target UI — dropdowns, right-click context menus — stays open and
    /// can be captured directly. Trade-off: single-key edit hotkeys / text annotation require one click on the
    /// overlay first. Translation mode is unaffected (keeps its own focus handling).
    /// </summary>
    public bool CaptureWithoutStealingFocus { get; set; } = true;
    /// <summary>
    /// When true, a screenshot (Normal/Copy/TextCopy/Pin) freezes the whole desktop into a still image the
    /// instant the overlay is triggered, and the user selects/annotates on that frozen image. This is the only
    /// reliable way to capture "light dismiss" shell UI — the Windows tray flyout, Start menu, and most
    /// left-click dropdowns — which close as soon as any full-screen overlay appears over them. Trade-off: the
    /// overlay shows a frozen still (not the live see-through), and clicking the live app inside the selection is
    /// disabled. Does not apply to Recording / Translation / scrolling capture (inherently live).
    /// </summary>
    public bool FreezeScreenOnScreenshot { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureDelay CaptureDelay { get; set; } = CaptureDelay.Off;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OcrTextLayout OcrTextLayout { get; set; } = OcrTextLayout.PreserveLines;
    /// <summary>When true, quick-OCR (Shift+F4) also writes the recognized text to a .txt file in SaveDirectory.</summary>
    public bool SaveOcrTextToFile { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScrollingCaptureDirection ScrollingCaptureDirection { get; set; } = ScrollingCaptureDirection.Auto;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SnipToolbarPosition SnipToolbarPosition { get; set; } = SnipToolbarPosition.TopCenter;

    public bool HideRecordPinDecoration { get; set; } = false;
    public bool HideRecordPinBorder { get; set; } = false;
    public bool HideRecordSelectionDecoration { get; set; } = false;
    
    // Default State
    public bool DefaultHideSnipToolbar { get; set; } = false;
    public bool DefaultHideRecordToolbar { get; set; } = false;
    
    // Output
    public bool AutoSave { get; set; }
    public string SaveDirectory { get; set; } = "";
    /// <summary>File-name template for saved captures/recordings ({date} {time} {datetime} {yyyy} {MM} {dd} {HH} {mm} {ss}); blank = the default "GimmeCapture_{date}_{time}".</summary>
    public string FileNameTemplate { get; set; } = "";
    /// <summary>User's own Imgur application Client-ID (https://api.imgur.com/oauth2/addclient) for
    /// the one-click screenshot upload; blank disables the feature. Not a secret by Imgur's design.</summary>
    public string ImgurClientId { get; set; } = "";
    /// <summary>When true, saved screenshots and finalized recordings are recorded in the capture history panel.</summary>
    public bool EnableHistory { get; set; } = true;
    /// <summary>When true, the saved file is revealed in File Explorer after a screenshot save or recording finish.</summary>
    public bool RevealAfterSave { get; set; } = true;
    
    public bool ShowSnipCursor { get; set; } = false;
    public bool ShowRecordCursor { get; set; } = true;
    // Burn a cursor spotlight ring / click ripple into recordings (tutorial highlighting).
    public bool HighlightCursor { get; set; } = false;
    public bool HighlightClicks { get; set; } = false;
    public bool ShowKeystrokes { get; set; } = false; // burn pressed-key chords into recordings
    // Experimental: pipeline capture/composite and encoding on separate threads (region/monitor recording).
    public bool PipelinedEncoding { get; set; } = false;
    public bool RecordSystemAudio { get; set; } = true;
    // Webcam picture-in-picture composited into recordings.
    public bool EnableWebcam { get; set; } = false;
    public string WebcamDeviceName { get; set; } = string.Empty;
    public int WebcamCorner { get; set; } = 3; // 0=TL, 1=TR, 2=BL, 3=BR
    public int WebcamSize { get; set; } = 1;   // 0=Small, 1=Medium, 2=Large (PiP width as a % of frame)
    public bool WebcamCircular { get; set; } = false; // clip the PiP to a circle with a ring border
    // Microphone capture, mixed with system audio at finalize.
    public bool RecordMicrophone { get; set; } = false;
    // WASAPI capture-endpoint device id (empty = default input device).
    public string SelectedMicDeviceId { get; set; } = string.Empty;
    // Linear gain applied to the mic stream when mixing (1.0 = unchanged).
    public double MicVolume { get; set; } = 1.0;
    public string VideoSaveDirectory { get; set; } = string.Empty;
    public string RecordFormat { get; set; } = "mp4";
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264;
    public VideoEncoderHint VideoEncoderHint { get; set; } = VideoEncoderHint.PreferHardware;
    public VideoQuality VideoQuality { get; set; } = VideoQuality.Medium;
    // Advanced encode overrides for recording (0 = auto): software CRF (1-51) / hardware bitrate (kbps).
    public int CustomVideoCrf { get; set; } = 0;
    public int CustomVideoBitrateKbps { get; set; } = 0;
    public int RecordFPS { get; set; } = 30;
    public double MaxRecordingSizeMB { get; set; } = 0;
    /// <summary>Auto-stop: maximum recorded length in seconds (pause time excluded); 0 = no limit.</summary>
    public int MaxRecordingSeconds { get; set; } = 0;
    /// <summary>What happens to the clip when <see cref="MaxRecordingSeconds"/> is reached.</summary>
    public RecordingAutoStopAction RecordingAutoStopAction { get; set; } = RecordingAutoStopAction.Pin;
    // Pinned video playback UI throttling (higher FPS = smoother, more UI load)
    public int PlaybackUiFps { get; set; } = 30;
    public int PlaybackTimelineFps { get; set; } = 15;
    // GPU (D3D11VA) hardware decode for video playback (editor / Pin / compare). On by default with automatic
    // software fallback; turn off if a GPU driver misbehaves.
    public bool HardwareDecodeEnabled { get; set; } = true;
    public bool UseFixedRecordPath { get; set; }
    public string TempDirectory { get; set; } = string.Empty;
    
    // Global Launch Hotkeys
    public string SnipHotkey { get; set; } = "Shift+F1";
    public string RecordHotkey { get; set; } = "Shift+F2";
    public string TranslateHotkey { get; set; } = "Shift+F3";
    public string TextCopyHotkey { get; set; } = "Shift+F4";
    public string ScrollingCaptureHotkey { get; set; } = "Shift+F5";

    // Mode Specific Hotkeys (New Structured Way)
    public SnipHotkeys Snip { get; set; } = new();
    public RecordHotkeys Record { get; set; } = new();
    public TranslateHotkeys Translate { get; set; } = new();

    // AI
    public string AIResourcesDirectory { get; set; } = string.Empty;
    public bool EnableAI { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SAM2Variant SelectedSAM2Variant { get; set; } = SAM2Variant.Tiny;
    public bool ShowAIScanBox { get; set; } = true;
    public bool EnableAIScan { get; set; } = true;
    [JsonIgnore]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AIScanEngine AIScanEngine { get; set; } = AIScanEngine.OCR;
    [JsonIgnore]
    public int SAM2GridDensity { get; set; } = 8;
    [JsonIgnore]
    public int SAM2MaxObjects { get; set; } = 20;
    [JsonIgnore]
    public int SAM2MinObjectSize { get; set; } = 20;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OCRLanguage SourceLanguage { get; set; } = OCRLanguage.Auto;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TranslationLanguage TargetLanguage { get; set; } = TranslationLanguage.TraditionalChinese;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TranslationEngine SelectedTranslationEngine { get; set; } = TranslationEngine.LlamaSharp;
    public string LlamaModelId { get; set; } = "translategemma-4b-it";
    public string LlamaCustomModelPath { get; set; } = string.Empty;
    public int LlamaContextSize { get; set; } = 2048;
    public int LlamaGpuLayers { get; set; } = 0;
}
