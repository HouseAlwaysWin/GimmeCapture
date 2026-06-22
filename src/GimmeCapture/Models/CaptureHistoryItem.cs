using System;
using System.Text.Json.Serialization;

namespace GimmeCapture.Models;

public enum CaptureHistoryKind
{
    Image,
    Video
}

/// <summary>
/// One entry in the capture history index. Only captures that were persisted to disk
/// (auto/manual-saved screenshots and finalized recordings) are recorded.
/// </summary>
public sealed class CaptureHistoryItem
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Absolute path to the saved capture (image or video) on disk.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Absolute path to the generated PNG thumbnail, or null if it could not be produced.</summary>
    public string? ThumbnailPath { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureHistoryKind Kind { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>File name without directory, derived from <see cref="FilePath"/> for display/search.</summary>
    [JsonIgnore]
    public string FileName =>
        string.IsNullOrEmpty(FilePath) ? string.Empty : System.IO.Path.GetFileName(FilePath);
}
