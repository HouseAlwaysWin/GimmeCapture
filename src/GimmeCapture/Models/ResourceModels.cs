namespace GimmeCapture.Models;

public class ResourceReadyResult
{
    public bool IsReady { get; set; }
    public string? ErrorKey { get; set; }
    public bool ShowDownloadPrompt { get; set; }

    public static ResourceReadyResult Ready() => new() { IsReady = true };
    
    public static ResourceReadyResult NotReady(string errorKey, bool showDownloadPrompt = false) 
        => new() { IsReady = false, ErrorKey = errorKey, ShowDownloadPrompt = showDownloadPrompt };
}
