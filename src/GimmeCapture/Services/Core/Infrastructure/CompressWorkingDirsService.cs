using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Persisted state for the Compress batch working directories: the folder list + the active one.</summary>
internal sealed class CompressWorkingDirsState
{
    public List<string> Directories { get; set; } = new();
    public string? Active { get; set; }
}

/// <summary>
/// Persists the Compress tab's batch "working directories" (source folders) + which one is active, under the
/// app storage root, so they survive restarts and the active folder's videos can auto-load on startup.
/// Failures are logged and degrade to empty / no-op.
/// </summary>
internal static class CompressWorkingDirsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-workdirs.json");

    public static CompressWorkingDirsState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new CompressWorkingDirsState();
            }

            string json = File.ReadAllText(FilePath);

            // Current format: { Directories, Active }.
            try
            {
                CompressWorkingDirsState? state = JsonSerializer.Deserialize<CompressWorkingDirsState>(json);
                if (state?.Directories != null)
                {
                    return state;
                }
            }
            catch (JsonException)
            {
                // fall through to the legacy format
            }

            // Legacy format: a bare array of paths.
            List<string>? legacy = JsonSerializer.Deserialize<List<string>>(json);
            return legacy != null ? new CompressWorkingDirsState { Directories = legacy } : new CompressWorkingDirsState();
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressWorkingDirs.Load", ex);
            return new CompressWorkingDirsState();
        }
    }

    public static void Save(CompressWorkingDirsState state)
    {
        try
        {
            Directory.CreateDirectory(AppStoragePaths.GetRootDirectory());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressWorkingDirs.Save", ex);
        }
    }
}
