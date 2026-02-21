using GimmeCapture.Services.Core;

namespace GimmeCapture.Models;

public enum VideoCodec { H264, H265 }
public enum TranslationLanguage { TraditionalChinese, SimplifiedChinese, English, Japanese, Korean }
public enum OCRLanguage { Auto, English, TraditionalChinese, SimplifiedChinese, Japanese, Korean }
public enum TranslationEngine { Ollama, MarianMT }

public class TranslatedBlock
{
    public string OriginalText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public Avalonia.Rect Bounds { get; set; }
    public double InferredFontSize { get; set; } = 12.0;
}

public class AppSettings
{
    public Language Language { get; set; } = Language.English;
    public bool RunOnStartup { get; set; }
    public bool AutoCheckUpdates { get; set; }
    
    // Snip
    public double BorderThickness { get; set; } = 2.0;
    public double MaskOpacity { get; set; } = 0.5;
    public string BorderColorHex { get; set; } = "#E60012";
    public string ThemeColorHex { get; set; } = "#E60012";
    public double WingScale { get; set; } = 1.0;
    public double CornerIconScale { get; set; } = 1.0;
    // Visibility
    public bool HideSnipPinDecoration { get; set; } = false;
    public bool HideSnipPinBorder { get; set; } = false;
    public bool HideSnipSelectionDecoration { get; set; } = false;
    public bool HideSnipSelectionBorder { get; set; } = false;

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
    public string VideoSaveDirectory { get; set; } = string.Empty;
    public string RecordFormat { get; set; } = "gif";
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264;
    public int RecordFPS { get; set; } = 30;
    public bool UseFixedRecordPath { get; set; }
    public string TempDirectory { get; set; } = string.Empty;
    
    // Hotkeys
    // Global Hotkeys
    public string SnipHotkey { get; set; } = "F1";
    public string RecordHotkey { get; set; } = "F2";
    public string TranslateHotkey { get; set; } = "F3";

    // Snip Mode Hotkeys
    public string SnipRectangleHotkey { get; set; } = "R";
    public string SnipEllipseHotkey { get; set; } = "E";
    public string SnipArrowHotkey { get; set; } = "A";
    public string SnipLineHotkey { get; set; } = "L";
    public string SnipPenHotkey { get; set; } = "P";
    public string SnipTextHotkey { get; set; } = "T";
    public string SnipMosaicHotkey { get; set; } = "M";
    public string SnipBlurHotkey { get; set; } = "B";
    public string SnipUndoHotkey { get; set; } = "Ctrl+Z";
    public string SnipRedoHotkey { get; set; } = "Ctrl+Y";
    public string SnipClearHotkey { get; set; } = "Delete";
    public string SnipSaveHotkey { get; set; } = "Ctrl+S";
    public string SnipCopyHotkey { get; set; } = "Ctrl+C";
    public string SnipPinHotkey { get; set; } = "F3";
    public string SnipCloseHotkey { get; set; } = "Escape";
    public string SnipToolbarHotkey { get; set; } = "F4";
    public string SnipSelectionModeHotkey { get; set; } = "S";
    public string SnipCropModeHotkey { get; set; } = "C";

    // Record Mode Hotkeys
    public string RecordRectangleHotkey { get; set; } = "R";
    public string RecordEllipseHotkey { get; set; } = "E";
    public string RecordArrowHotkey { get; set; } = "A";
    public string RecordLineHotkey { get; set; } = "L";
    public string RecordPenHotkey { get; set; } = "P";
    public string RecordTextHotkey { get; set; } = "T";
    public string RecordMosaicHotkey { get; set; } = "M";
    public string RecordBlurHotkey { get; set; } = "B";
    public string RecordUndoHotkey { get; set; } = "Ctrl+Z";
    public string RecordRedoHotkey { get; set; } = "Ctrl+Y";
    public string RecordClearHotkey { get; set; } = "Delete";
    public string RecordSaveHotkey { get; set; } = "Ctrl+S";
    public string RecordCopyHotkey { get; set; } = "Ctrl+C";
    public string RecordCloseHotkey { get; set; } = "Escape";
    public string RecordToolbarHotkey { get; set; } = "F4";
    public string RecordActionHotkey { get; set; } = "F3";
    public string RecordPlaybackHotkey { get; set; } = "Space";

    // Translate Mode Hotkeys
    public string TranslateActionHotkey { get; set; } = "F3";
    public string TranslateToolbarHotkey { get; set; } = "F4";
    public string TranslateCloseHotkey { get; set; } = "Escape";
    
    // AI
    public string AIResourcesDirectory { get; set; } = string.Empty;
    public bool EnableAI { get; set; } = true;
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public SAM2Variant SelectedSAM2Variant { get; set; } = SAM2Variant.Tiny;
    public bool ShowAIScanBox { get; set; } = true;
    public bool EnableAIScan { get; set; } = true;
    public int SAM2GridDensity { get; set; } = 8;
    public int SAM2MaxObjects { get; set; } = 20;
    public int SAM2MinObjectSize { get; set; } = 20;
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public OCRLanguage SourceLanguage { get; set; } = OCRLanguage.Auto;
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public TranslationLanguage TargetLanguage { get; set; } = TranslationLanguage.TraditionalChinese;
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public TranslationEngine SelectedTranslationEngine { get; set; } = TranslationEngine.Ollama;
    public string OllamaModel { get; set; } = "";
    public string OllamaApiUrl { get; set; } = "http://localhost:11434/api/generate";
}
