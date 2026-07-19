using System;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class CaptureFileNameService
{
    /// <summary>The template that reproduces the historical fixed naming exactly.</summary>
    public const string DefaultTemplate = "GimmeCapture_{date}_{time}";

    public static string SuggestedBaseName()
    {
        return $"GimmeCapture_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    /// <summary>Template-aware variant: renders the user's file-name template with the current time.</summary>
    public static string SuggestedBaseName(string? template)
    {
        return RenderTemplate(template, DateTime.Now);
    }

    public static string BuildFileName(string extensionWithoutDot)
    {
        return BuildFileName(extensionWithoutDot, null);
    }

    /// <summary>Template-aware variant used by the save/auto-save call sites.</summary>
    public static string BuildFileName(string extensionWithoutDot, string? template)
    {
        var ext = extensionWithoutDot?.Trim().TrimStart('.') ?? string.Empty;
        var baseName = SuggestedBaseName(template);
        return string.IsNullOrWhiteSpace(ext)
            ? baseName
            : $"{baseName}.{ext}";
    }

    /// <summary>
    /// Renders a user file-name template into a safe file BASE name (no extension). Supported tokens:
    /// {date}=yyyyMMdd, {time}=HHmmss, {datetime}=yyyyMMdd_HHmmss, and the individual {yyyy} {MM} {dd}
    /// {HH} {mm} {ss} parts. Unknown tokens stay as literal text, path-invalid characters are
    /// sanitized, and a blank/degenerate result falls back to <see cref="DefaultTemplate"/> so a broken
    /// template can never produce an unusable name. Pure (caller supplies the clock) for testability.
    /// </summary>
    public static string RenderTemplate(string? template, DateTime now)
    {
        string effective = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template!;

        string safe = CompressOutputPath.SanitizeFileName(ReplaceTokens(effective, now));
        if (safe.Length == 0)
        {
            // Degenerate template (all-invalid / whitespace after rendering): never emit an empty name.
            safe = CompressOutputPath.SanitizeFileName(ReplaceTokens(DefaultTemplate, now));
        }

        return safe;
    }

    private static string ReplaceTokens(string template, DateTime now) =>
        template
            .Replace("{datetime}", now.ToString("yyyyMMdd_HHmmss"))
            .Replace("{date}", now.ToString("yyyyMMdd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Replace("{yyyy}", now.ToString("yyyy"))
            .Replace("{MM}", now.ToString("MM"))
            .Replace("{dd}", now.ToString("dd"))
            .Replace("{HH}", now.ToString("HH"))
            .Replace("{mm}", now.ToString("mm"))
            .Replace("{ss}", now.ToString("ss"));
}
