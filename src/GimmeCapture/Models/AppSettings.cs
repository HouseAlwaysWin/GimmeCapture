namespace GimmeCapture.Models;

public class AppSettings
{
    public string Language { get; set; } = "zh-TW";
    public bool RunOnStartup { get; set; }
    public bool AutoCheckUpdates { get; set; }
    
    // Snip
    public double BorderThickness { get; set; } = 2.0;
    public double MaskOpacity { get; set; } = 0.5;
    public string BorderColorHex { get; set; } = "#E60012";
    
    // Output
    public bool AutoSave { get; set; }
    public string SaveDirectory { get; set; } = string.Empty;
    
    // Hotkeys
    public string SnipHotkey { get; set; } = "F1";
    public string CopyHotkey { get; set; } = "Ctrl+C";
    public string PinHotkey { get; set; } = "F3";
}
