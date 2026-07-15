using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Loads/saves the user's Compress-tab presets as a single JSON list under the app storage root
/// (shared across versions/instances). All failures are logged and degrade to an empty list / no-op.
/// </summary>
internal static class CompressPresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-presets.json");

    public static List<CompressPreset> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<CompressPreset>();
            }

            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<CompressPreset>>(json) ?? new List<CompressPreset>();
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressPresets.Load", ex);
            return new List<CompressPreset>();
        }
    }

    public static void Save(IEnumerable<CompressPreset> presets)
    {
        try
        {
            Directory.CreateDirectory(AppStoragePaths.GetRootDirectory());
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(presets, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressPresets.Save", ex);
        }
    }
}
