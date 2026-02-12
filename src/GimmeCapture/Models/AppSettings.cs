using GimmeCapture.Services.Core;

namespace GimmeCapture.Models;

public enum VideoCodec { H264, H265 }

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
    public string PinHotkey { get; set; } = "F3";
    public string CopyHotkey { get; set; } = "Ctrl+C";

    // Drawing Tool Hotkeys
    public string RectangleHotkey { get; set; } = "R";
    public string EllipseHotkey { get; set; } = "E";
    public string ArrowHotkey { get; set; } = "A";
    public string LineHotkey { get; set; } = "L";
    public string PenHotkey { get; set; } = "P";
    public string TextHotkey { get; set; } = "T";
    public string MosaicHotkey { get; set; } = "M";
    public string BlurHotkey { get; set; } = "B";

    // Action Hotkeys
    public string UndoHotkey { get; set; } = "Ctrl+Z";
    public string RedoHotkey { get; set; } = "Ctrl+Y";
    public string ClearHotkey { get; set; } = "Delete";
    public string SaveHotkey { get; set; } = "Ctrl+S";
    public string CloseHotkey { get; set; } = "Escape";
    public string TogglePlaybackHotkey { get; set; } = "Space";
    public string ToggleToolbarHotkey { get; set; } = "F4";
    public string SelectionModeHotkey { get; set; } = "S";
    public string CropModeHotkey { get; set; } = "C";
    
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
    public bool AutoTranslate { get; set; } = false;
}
