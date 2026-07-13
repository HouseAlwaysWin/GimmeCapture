namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Human-readable file-size formatting (B / KB / MB / GB). Shared by the compress-queue model and VM.</summary>
public static class FileSizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.0} {units[unit]}";
    }
}
