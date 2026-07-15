using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Resume state for one CRF-mode segmented compression: which time-chunks are already encoded, the settings
/// they were encoded with, and the target output. Persisted next to the chunk files so an interrupted encode
/// can continue after an app restart.
/// </summary>
public sealed class CompressSegmentState
{
    public string InputPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Encode settings the chunks were produced with; a mismatch invalidates the chunks.</summary>
    public string SettingsKey { get; set; } = string.Empty;

    public double TotalDuration { get; set; }
    public double ChunkSeconds { get; set; }
    public int ChunkCount { get; set; }

    public List<int> CompletedChunks { get; set; } = new();
}

/// <summary>
/// Owns the on-disk segment directories under <c>{appRoot}\CompressSegments\{hash(input)}</c>: the chunk
/// files (<c>chunk_N.mp4</c>) plus a <c>state.json</c>. Mirrors the JSON Load/Save + AppLog pattern of the
/// other compress stores; all failures degrade to null / no-op.
/// </summary>
internal static class CompressSegmentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Orphaned segment dirs (crashed/abandoned runs) older than this are pruned on startup.
    private static readonly TimeSpan OrphanMaxAge = TimeSpan.FromDays(7);

    private static string RootDir =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "CompressSegments");

    public static string DirForInput(string inputPath) =>
        Path.Combine(RootDir, HashInput(inputPath));

    public static string ChunkPath(string inputPath, int index) =>
        Path.Combine(DirForInput(inputPath), $"chunk_{index}.mp4");

    private static string StateFile(string inputPath) =>
        Path.Combine(DirForInput(inputPath), "state.json");

    public static CompressSegmentState? Load(string inputPath)
    {
        try
        {
            string file = StateFile(inputPath);
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CompressSegmentState>(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSegment.Load", ex);
            return null;
        }
    }

    public static void Save(CompressSegmentState state)
    {
        try
        {
            Directory.CreateDirectory(DirForInput(state.InputPath));
            AtomicFile.WriteAllText(StateFile(state.InputPath), JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSegment.Save", ex);
        }
    }

    public static void Clear(string inputPath)
    {
        try
        {
            string dir = DirForInput(inputPath);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSegment.Clear", ex);
        }
    }

    /// <summary>Removes abandoned segment dirs (no state.json, or older than <see cref="OrphanMaxAge"/>).</summary>
    public static void PruneOrphans()
    {
        try
        {
            if (!Directory.Exists(RootDir))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow - OrphanMaxAge;
            foreach (string dir in Directory.GetDirectories(RootDir))
            {
                string state = Path.Combine(dir, "state.json");
                bool stale = !File.Exists(state) || File.GetLastWriteTimeUtc(state) < cutoff;
                if (stale)
                {
                    try { Directory.Delete(dir, true); } catch { /* best effort */ }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSegment.Prune", ex);
        }
    }

    private static string HashInput(string inputPath)
    {
        string normalized;
        try { normalized = Path.GetFullPath(inputPath).ToLowerInvariant(); }
        catch { normalized = inputPath.ToLowerInvariant(); }

        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
