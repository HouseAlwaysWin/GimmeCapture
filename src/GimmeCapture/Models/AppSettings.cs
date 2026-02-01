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
    public bool ShowPinDecoration { get; set; } = true;
    
    // Output
    public bool AutoSave { get; set; }
    public string SaveDirectory { get; set; } = string.Empty;
    public string VideoSaveDirectory { get; set; } = string.Empty;
    public string RecordFormat { get; set; } = "mp4";
    public bool UseFixedRecordPath { get; set; }
    
    // Hotkeys
    public string SnipHotkey { get; set; } = "F1";
    public string CopyHotkey { get; set; } = "Ctrl+C";
    public string PinHotkey { get; set; } = "F3";
    public string RecordHotkey { get; set; } = "F2";
}
