using System;
using System.IO;
using System.Text.Json;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Last-used Compress settings, restored on launch so a resumed batch (and the user generally)
/// keeps the encode settings + batch options instead of falling back to defaults.</summary>
public sealed class CompressSessionState
{
    public CompressPreset Settings { get; set; } = new();
    public string OutputFolder { get; set; } = string.Empty;
    public bool AppendDate { get; set; } = true;
    public int ParallelCount { get; set; } = 2;
}

/// <summary>
/// Persists the last-used Compress settings to JSON under the app storage root. Failures log and degrade
/// to a default state / no-op, mirroring <see cref="CompressPresetService"/>.
/// </summary>
internal static class CompressSessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-session.json");

    public static CompressSessionState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new CompressSessionState();
            }

            return JsonSerializer.Deserialize<CompressSessionState>(File.ReadAllText(FilePath))
                   ?? new CompressSessionState();
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSession.Load", ex);
            return new CompressSessionState();
        }
    }

    public static void Save(CompressSessionState state)
    {
        try
        {
            Directory.CreateDirectory(AppStoragePaths.GetRootDirectory());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSession.Save", ex);
        }
    }
}
