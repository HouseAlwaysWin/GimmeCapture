using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>A video to queue, with what listing its folder already said about it.</summary>
internal readonly record struct QueuedVideo(string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// What a probe of a queued video found, kept so that the video need not be read again while it stays as it was. A queue
/// row needs its length, size, frame rate and picture, and getting them meant opening every file of the working folder
/// the first time the window opened in a run: 66-72 s for the user's 78 files on a hard disk.
/// </summary>
public sealed class CompressProbeEntry
{
    public long Length { get; set; }
    public long LastWriteUtcTicks { get; set; }

    /// <summary>When this was probed: of too many entries, the oldest go.</summary>
    public long ProbedUtcTicks { get; set; }

    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }

    public int ThumbnailWidth { get; set; }
    public int ThumbnailHeight { get; set; }
    public byte[]? ThumbnailJpeg { get; set; }

    internal bool Describes(long length, DateTime lastWriteUtc) => Length == length && LastWriteUtcTicks == lastWriteUtc.Ticks;
}

/// <summary>A queue row's small picture as JPEG, about 3 KB against 20 raw, and back as the BGRA a row is drawn from.</summary>
internal static class CompressThumbnailCodec
{
    private const int JpegQuality = 85;

    internal static unsafe byte[]? Encode(LibavVideoFramePlayer.BgraThumbnail thumbnail)
    {
        var info = new SKImageInfo(thumbnail.Width, thumbnail.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        fixed (byte* pixels = thumbnail.Bgra)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)pixels, thumbnail.Width * 4);
            using SKData? data = pixmap.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return data?.ToArray();
        }
    }

    /// <summary>Null when the entry holds no picture of the size it says.</summary>
    internal static LibavVideoFramePlayer.BgraThumbnail? Decode(CompressProbeEntry entry)
    {
        if (entry.ThumbnailJpeg is not { Length: > 0 } jpeg || entry.ThumbnailWidth <= 0 || entry.ThumbnailHeight <= 0)
        {
            return null;
        }

        var info = new SKImageInfo(entry.ThumbnailWidth, entry.ThumbnailHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKData data = SKData.CreateCopy(jpeg);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec == null)
        {
            return null;
        }

        using SKBitmap? bitmap = SKBitmap.Decode(codec, info);
        if (bitmap == null || bitmap.Width != info.Width || bitmap.Height != info.Height)
        {
            return null;
        }

        var bgra = new byte[info.BytesSize];
        bitmap.GetPixelSpan().Slice(0, bgra.Length).CopyTo(bgra);
        return new LibavVideoFramePlayer.BgraThumbnail(info.Width, info.Height, bgra);
    }
}

/// <summary>
/// Persists what probes found (source path → <see cref="CompressProbeEntry"/>) as JSON under the app storage root, about
/// 4 KB a video. Failures log and degrade to probing again.
/// </summary>
internal static class CompressProbeCacheService
{
    /// <summary>The most kept, so that the file stays a few MB however many folders pass through the queue.</summary>
    internal const int MaxEntries = 1000;

    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromSeconds(2); // a folder's probes finish one after another
    private static readonly Lazy<CompressProbeCacheWriter> SharedWriter = new(CreateSharedWriter);

    private static string FilePath => Path.Combine(AppStoragePaths.GetRootDirectory(), "compress-probe-cache.json");

    public static Dictionary<string, CompressProbeEntry> Load() => Load(FilePath);

    internal static Dictionary<string, CompressProbeEntry> Load(string filePath)
    {
        var map = new Dictionary<string, CompressProbeEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(filePath)
                && JsonSerializer.Deserialize<Dictionary<string, CompressProbeEntry>>(File.ReadAllBytes(filePath)) is { } saved)
            {
                foreach (KeyValuePair<string, CompressProbeEntry> entry in saved)
                {
                    if (entry.Value != null)
                    {
                        map[entry.Key] = entry.Value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("CompressProbeCache.Load", ex);
        }

        return map;
    }

    /// <summary>Has the map written shortly, off the calling thread, and returns at once.</summary>
    public static void Save(IReadOnlyDictionary<string, CompressProbeEntry> entries) => SharedWriter.Value.Save(entries);

    /// <summary>Writes a save still waiting for the background writer.</summary>
    public static void Flush()
    {
        if (SharedWriter.IsValueCreated)
        {
            SharedWriter.Value.Flush();
        }
    }

    internal static void Write(string filePath, IReadOnlyDictionary<string, CompressProbeEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(Newest(entries, MaxEntries)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("CompressProbeCache.Save", ex);
        }
    }

    internal static IReadOnlyDictionary<string, CompressProbeEntry> Newest(IReadOnlyDictionary<string, CompressProbeEntry> entries, int most) =>
        entries.Count <= most
            ? entries
            : entries.OrderByDescending(e => e.Value.ProbedUtcTicks).Take(most).ToDictionary(e => e.Key, e => e.Value);

    private static CompressProbeCacheWriter CreateSharedWriter()
    {
        string filePath = FilePath;
        var writer = new CompressProbeCacheWriter(entries => Write(filePath, entries), CoalesceDelay);
        // Nothing disposes the main view model, so the process exit is where a save still waiting gets written.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Flush();
        return writer;
    }
}

/// <summary>Writes the probes found off the caller's thread (<see cref="CoalescingWriter{T}"/>).</summary>
internal sealed class CompressProbeCacheWriter : CoalescingWriter<IReadOnlyDictionary<string, CompressProbeEntry>>
{
    public CompressProbeCacheWriter(Action<IReadOnlyDictionary<string, CompressProbeEntry>> write, TimeSpan coalesceDelay)
        : base(write, coalesceDelay)
    {
    }

    /// <summary>Keeps a copy of <paramref name="entries"/> to write shortly. An entry is replaced, never changed.</summary>
    public void Save(IReadOnlyDictionary<string, CompressProbeEntry> entries) =>
        SaveValue(new Dictionary<string, CompressProbeEntry>(entries, StringComparer.OrdinalIgnoreCase), "CompressProbeCache.Save");
}
