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

    /// <summary>Defaults schema version; a value below the app's current version triggers a one-time upgrade
    /// to the new code defaults on load (so changing the built-in defaults reaches existing users once).</summary>
    public int Version { get; set; }
}

/// <summary>
/// Persists the last-used Compress settings to JSON under the app storage root. Failures log and degrade
/// to a default state / no-op, mirroring <see cref="CompressPresetService"/>.
/// </summary>
internal static class CompressSessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // How long a save waits for more: dragging the CRF slider changes the settings at every step.
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(300);

    private static readonly Lazy<CompressSessionWriter> SharedWriter = new(CreateSharedWriter);

    private static string FilePath =>
        Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-session.json");

    public static CompressSessionState Load()
    {
        // A save still waiting is newer than the file.
        Flush();
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

    /// <summary>
    /// Has <paramref name="state"/> written shortly, off the calling thread, and returns at once. Each change of a
    /// Compress setting saved it on the UI thread, an atomic replace of the file that took 4-10 ms (up to 41 with the
    /// machine busy), at every step of a slider being dragged. The state must not be changed after it is handed over.
    /// </summary>
    public static void Save(CompressSessionState state) => SharedWriter.Value.Save(state);

    /// <summary>Writes a save still waiting, on the calling thread.</summary>
    internal static void Flush()
    {
        if (SharedWriter.IsValueCreated)
        {
            SharedWriter.Value.Flush();
        }
    }

    internal static void Write(string filePath, CompressSessionState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error("CompressSession.Save", ex);
        }
    }

    private static CompressSessionWriter CreateSharedWriter()
    {
        string filePath = FilePath;
        var writer = new CompressSessionWriter(state => Write(filePath, state), CoalesceDelay);
        // Nothing disposes the main view model, so the process exit is where a save still waiting gets written.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Flush();
        return writer;
    }
}

/// <summary>Writes the last-used Compress settings off the caller's thread (<see cref="CoalescingWriter{T}"/>).
/// </summary>
internal sealed class CompressSessionWriter : CoalescingWriter<CompressSessionState>
{
    public CompressSessionWriter(Action<CompressSessionState> write, TimeSpan coalesceDelay)
        : base(write, coalesceDelay)
    {
    }

    /// <summary>Keeps <paramref name="state"/>, which nothing changes after this, to write shortly.</summary>
    public void Save(CompressSessionState state) => SaveValue(state, "CompressSession.Save");
}
