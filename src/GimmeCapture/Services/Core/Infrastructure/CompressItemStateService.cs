using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Per-file Compress edit/progress state, keyed by source path: the user's rotation + output name
/// (so they survive restarts) and whether the file has already been compressed (so a resumed batch skips it).</summary>
public sealed class CompressItemState
{
    public int Rotation { get; set; }
    public string OutputName { get; set; } = string.Empty;
    public bool Done { get; set; }

    /// <summary>The file was paused when the app closed; on reload it shows as paused at <see cref="Progress"/>.</summary>
    public bool Paused { get; set; }

    /// <summary>Last progress (0–1) reached before pausing, shown when the file is restored as paused.</summary>
    public double Progress { get; set; }
}

/// <summary>
/// Persists the per-file Compress state map (source path → <see cref="CompressItemState"/>) as JSON under the
/// app storage root, so edit choices and completion survive restarts. Failures log and degrade to empty / no-op.
/// </summary>
internal static class CompressItemStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-item-states.json");

    /// <summary>Loads the map, dropping entries whose source file no longer exists (keeps the file from growing).</summary>
    public static Dictionary<string, CompressItemState> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new Dictionary<string, CompressItemState>(StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, CompressItemState>? raw =
                JsonSerializer.Deserialize<Dictionary<string, CompressItemState>>(File.ReadAllText(FilePath));

            var map = new Dictionary<string, CompressItemState>(StringComparer.OrdinalIgnoreCase);
            if (raw != null)
            {
                foreach (KeyValuePair<string, CompressItemState> kv in raw)
                {
                    if (kv.Value != null && File.Exists(kv.Key))
                    {
                        map[kv.Key] = kv.Value;
                    }
                }
            }

            return map;
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressItemState.Load", ex);
            return new Dictionary<string, CompressItemState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void Save(IReadOnlyDictionary<string, CompressItemState> states)
    {
        try
        {
            Directory.CreateDirectory(AppStoragePaths.GetRootDirectory());
            File.WriteAllText(FilePath, JsonSerializer.Serialize(states, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressItemState.Save", ex);
        }
    }
}
