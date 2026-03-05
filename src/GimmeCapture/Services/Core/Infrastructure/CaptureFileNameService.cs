using System;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class CaptureFileNameService
{
    public static string SuggestedBaseName()
    {
        return $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    public static string BuildFileName(string extensionWithoutDot)
    {
        var ext = extensionWithoutDot?.Trim().TrimStart('.') ?? string.Empty;
        return string.IsNullOrWhiteSpace(ext)
            ? SuggestedBaseName()
            : $"{SuggestedBaseName()}.{ext}";
    }
}
