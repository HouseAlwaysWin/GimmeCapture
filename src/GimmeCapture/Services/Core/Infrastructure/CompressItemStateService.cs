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

    /// <summary>Per-file trim on/off. When on, <see cref="Segments"/> holds the kept runs concatenated on encode.</summary>
    public bool TrimEnabled { get; set; }

    /// <summary>The kept runs (source ranges) to concatenate; null = whole clip. Multi-segment shape.</summary>
    public List<TrimSegment>? Segments { get; set; }

    // Legacy single-range trim (pre-multi-segment). Kept so old state files still load; migrated into Segments.
    public decimal TrimStart { get; set; }
    public decimal TrimEnd { get; set; }
}

/// <summary>One kept run (source [Start, End] seconds) of a multi-segment trim.</summary>
public sealed class TrimSegment
{
    public decimal Start { get; set; }
    public decimal End { get; set; }
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
                        MigrateLegacyTrim(kv.Value);
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

    // Migrate the legacy single [TrimStart,TrimEnd] into a one-run Segments list (only when Segments is unset).
    private static void MigrateLegacyTrim(CompressItemState st)
    {
        if (st.Segments is { Count: > 0 })
        {
            return;
        }
        if (st.TrimEnabled && st.TrimEnd > st.TrimStart)
        {
            st.Segments = new List<TrimSegment> { new() { Start = st.TrimStart, End = st.TrimEnd } };
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
