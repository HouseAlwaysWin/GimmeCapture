using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>Per-file Compress edit/progress state, keyed by source path: the user's rotation + output name
/// (so they survive restarts) and whether the file has already been compressed (so a resumed batch skips it).</summary>
public sealed class CompressItemState
{
    public int Rotation { get; set; }
    public string OutputName { get; set; } = string.Empty;
    public bool Done { get; set; }

    /// <summary>Full path of the last produced output file (set on success). Drives the "open output folder" button.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Per-file chosen output folder (a working directory); empty/null = global/next-to-source default.</summary>
    public string? SelectedOutputDir { get; set; }

    // Optional per-file crop (source pixels). A zero-size rect means "no crop". Applied before rotation at encode.
    public int CropX { get; set; }
    public int CropY { get; set; }
    public int CropWidth { get; set; }
    public int CropHeight { get; set; }

    /// <summary>The file was paused when the app closed; on reload it shows as paused at <see cref="Progress"/>.</summary>
    public bool Paused { get; set; }

    /// <summary>Last progress (0–1) reached before pausing, shown when the file is restored as paused.</summary>
    public double Progress { get; set; }

    /// <summary>Per-file trim on/off. When on, <see cref="Segments"/> holds the kept runs concatenated on encode.</summary>
    public bool TrimEnabled { get; set; }

    /// <summary>The kept runs (source ranges) to concatenate; null = whole clip. Multi-segment shape.</summary>
    public List<TrimSegment>? Segments { get; set; }

    // Burn-in layers from the 進階影片編輯 editor: annotations (in surface coords — the cropped+rotated
    // preview-frame size below) and redaction tracks (normalized [0,1]). Null = none.
    public List<CompressAnnotationState>? Annotations { get; set; }
    public double AnnotationSurfaceWidth { get; set; }
    public double AnnotationSurfaceHeight { get; set; }
    public List<CompressRedactionTrackState>? RedactionTracks { get; set; }

    // Legacy single-range trim (pre-multi-segment). Kept so old state files still load; migrated into Segments.
    public decimal TrimStart { get; set; }
    public decimal TrimEnd { get; set; }
}

/// <summary>One kept run (source [Start, End] seconds) of a multi-segment edit, optionally time-scaled.</summary>
public sealed class TrimSegment
{
    public decimal Start { get; set; }
    public decimal End { get; set; }

    /// <summary>Playback speed for this run (1 = normal); &gt;1 faster, &lt;1 slow-motion.</summary>
    public decimal Speed { get; set; } = 1m;
}

/// <summary>
/// Persists the per-file Compress state map (source path → <see cref="CompressItemState"/>) as JSON under the
/// app storage root, so edit choices and completion survive restarts. Failures log and degrade to empty / no-op.
/// </summary>
internal static class CompressItemStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // How long a save waits for more: applying the editor's edits persists the map once per changed item property.
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(300);

    private static readonly Lazy<CompressItemStateWriter> SharedWriter = new(CreateSharedWriter);

    private static string FilePath =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-item-states.json");

    /// <summary>Loads the map, dropping entries whose source file no longer exists (keeps the file from growing).</summary>
    public static Dictionary<string, CompressItemState> Load() => Load(FilePath);

    internal static Dictionary<string, CompressItemState> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new Dictionary<string, CompressItemState>(StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, CompressItemState>? raw =
                JsonSerializer.Deserialize<Dictionary<string, CompressItemState>>(File.ReadAllText(filePath));

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

    /// <summary>
    /// Has the map written to disk shortly, off the calling thread (see <see cref="CompressItemStateWriter"/>), and
    /// returns at once.
    /// </summary>
    public static void Save(IReadOnlyDictionary<string, CompressItemState> states) => SharedWriter.Value.Save(states);

    /// <summary>Writes a save still waiting for the background writer, on a worker thread.</summary>
    public static Task FlushAsync() =>
        SharedWriter.IsValueCreated ? Task.Run(SharedWriter.Value.Flush) : Task.CompletedTask;

    internal static void Write(string filePath, IReadOnlyDictionary<string, CompressItemState> states)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(states, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressItemState.Save", ex);
        }
    }

    private static CompressItemStateWriter CreateSharedWriter()
    {
        string filePath = FilePath;
        var writer = new CompressItemStateWriter(states => Write(filePath, states), CoalesceDelay);
        // Nothing disposes the main view model, so the process exit is where a save still waiting gets written.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Flush();
        return writer;
    }
}

/// <summary>
/// Writes the per-file Compress state map off the caller's thread. Every edit to a queue item persists the whole map,
/// and it used to rewrite the indented JSON on the UI thread each time, several times over when the editor's edits
/// were applied. <see cref="Save"/> now keeps only the newest map: the first save starts a short wait, saves during
/// it replace the map waiting, and one write follows. Writes never overlap and never put an older map over a newer
/// one. <see cref="Flush"/> writes whatever is still waiting, at once.
/// </summary>
internal sealed class CompressItemStateWriter
{
    private readonly Action<IReadOnlyDictionary<string, CompressItemState>> _write;
    private readonly TimeSpan _coalesceDelay;
    private readonly object _pendingLock = new();
    private readonly object _writeLock = new(); // held through a write: one at a time, each taking the newest map
    private Dictionary<string, CompressItemState>? _pending;
    private bool _writeScheduled;

    public CompressItemStateWriter(Action<IReadOnlyDictionary<string, CompressItemState>> write, TimeSpan coalesceDelay)
    {
        _write = write;
        _coalesceDelay = coalesceDelay;
    }

    /// <summary>Keeps a copy of <paramref name="states"/> to write shortly; returns without waiting for any write.</summary>
    public void Save(IReadOnlyDictionary<string, CompressItemState> states)
    {
        // Copied on the caller's thread, which goes on changing its map. The states in it are replaced on an edit,
        // never changed, so a shallow copy holds still while it is written.
        var snapshot = new Dictionary<string, CompressItemState>(states);
        lock (_pendingLock)
        {
            _pending = snapshot;
            if (_writeScheduled)
            {
                return;
            }

            _writeScheduled = true;
        }

        Task.Run(WriteAfterDelayAsync).Forget("CompressItemState.Save");
    }

    /// <summary>Writes the map still waiting, if any, on the calling thread, after a write already under way.</summary>
    public void Flush() => WritePending();

    private async Task WriteAfterDelayAsync()
    {
        await Task.Delay(_coalesceDelay).ConfigureAwait(false);
        WritePending();
    }

    private void WritePending()
    {
        lock (_writeLock)
        {
            Dictionary<string, CompressItemState>? states;
            lock (_pendingLock)
            {
                states = _pending;
                _pending = null;
                _writeScheduled = false; // a save from here on needs a write of its own
            }

            if (states != null)
            {
                _write(states);
            }
        }
    }
}
