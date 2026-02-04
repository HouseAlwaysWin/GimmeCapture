using GimmeCapture.Services;

namespace GimmeCapture.Models;

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
    public bool UseFixedRecordPath { get; set; }
    public string TempDirectory { get; set; } = string.Empty;
    
    // Hotkeys
    public string SnipHotkey { get; set; } = "F1";
    public string CopyHotkey { get; set; } = "Ctrl+C";
    public string PinHotkey { get; set; } = "F3";
    public string RecordHotkey { get; set; } = "F2";
}
