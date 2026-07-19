using System;
using System.IO;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Writes a recognized OCR text to a .txt file in the user's save directory, named by the
/// file-name template. Path composition is pure/static for testability; only WriteAsync touches disk.
/// </summary>
public static class OcrTextExportService
{
    /// <summary>Renders the template into the export file name (no directory), e.g. "GimmeCapture_20260629_143052.txt".</summary>
    public static string BuildExportFileName(string? template, DateTime now) =>
        CaptureFileNameService.RenderTemplate(template, now) + ".txt";

    /// <summary>
    /// Picks the first non-colliding path for <paramref name="fileName"/> under <paramref name="directory"/>,
    /// using the same " (n)" suffix convention as the compress output path. Pure over the supplied
    /// exists-probe so tests don't need a real file system.
    /// </summary>
    public static string ResolveCollisionFreePath(string directory, string fileName, Func<string, bool> exists)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        string candidate = Path.Combine(directory, fileName);
        for (int n = 1; exists(candidate); n++)
        {
            candidate = Path.Combine(directory, $"{baseName} ({n}){ext}");
        }

        return candidate;
    }

    /// <summary>
    /// Writes <paramref name="text"/> as UTF-8 into <paramref name="directory"/> using the template-rendered
    /// name (collision-safe). Returns the written path, or null on failure (logged, never throws — export is
    /// a best-effort side channel next to the clipboard copy).
    /// </summary>
    public static async Task<string?> WriteAsync(string text, string directory, string? template)
    {
        try
        {
            FileLocationService.EnsureDirectory(directory, "OcrTextExport.EnsureDirectory");
            string path = ResolveCollisionFreePath(directory, BuildExportFileName(template, DateTime.Now), File.Exists);
            await File.WriteAllTextAsync(path, text);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warning("OcrTextExport.Write", ex);
            return null;
        }
    }
}
