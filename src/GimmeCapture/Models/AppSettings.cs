using GimmeCapture.Services.Core;
using System.Text.Json.Serialization;

namespace GimmeCapture.Models;

public enum VideoCodec { H264, H265 }
public enum VideoQuality { Low, Medium, High }
public enum TranslationLanguage { TraditionalChinese, SimplifiedChinese, English, Japanese, Korean }
public enum OCRLanguage { Auto, English, TraditionalChinese, SimplifiedChinese, Japanese, Korean }
public enum TranslationEngine { LlamaSharp, Ollama, MarianMT }
public enum AIScanEngine { OCR, SAM2 }

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
    public bool HideSnipSelectionBorder { get; set; } = false;
    public bool AutoPinScreenshotSelection { get; set; } = false;

    public bool HideRecordPinDecoration { get; set; } = false;
    public bool HideRecordPinBorder { get; set; } = false;
    public bool HideRecordSelectionDecoration { get; set; } = false;
    public bool HideRecordSelectionBorder { get; set; } = false;
    
    // Default State
    public bool DefaultHideSnipToolbar { get; set; } = false;
    public bool DefaultHideRecordToolbar { get; set; } = false;
    
    // Output
    public bool AutoSave { get; set; }
    public string SaveDirectory { get; set; } = "";
    
    public bool ShowSnipCursor { get; set; } = false;
    public bool ShowRecordCursor { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public string VideoSaveDirectory { get; set; } = string.Empty;
    public string RecordFormat { get; set; } = "mp4";
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264;
    public VideoQuality VideoQuality { get; set; } = VideoQuality.Medium;
    public int RecordFPS { get; set; } = 30;
    public double MaxRecordingSizeMB { get; set; } = 0;
    // Pinned video playback UI throttling (higher FPS = smoother, more UI load)
    public int PlaybackUiFps { get; set; } = 30;
    public int PlaybackTimelineFps { get; set; } = 15;
    public bool UseFixedRecordPath { get; set; }
    public string TempDirectory { get; set; } = string.Empty;
    
    // Global Launch Hotkeys
    public string SnipHotkey { get; set; } = "Shift+F1";
    public string RecordHotkey { get; set; } = "Shift+F2";
    public string TranslateHotkey { get; set; } = "Shift+F3";

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
