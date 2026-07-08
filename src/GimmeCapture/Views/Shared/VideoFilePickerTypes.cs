using System.Collections.Generic;
using Avalonia.Platform.Storage;

namespace GimmeCapture.Views.Shared;

/// <summary>
/// The single home for the file-picker type tables that were previously hand-rolled (with divergent
/// extension lists) in FloatingVideoWindow, SnipWindow.ViewModelWiring and SettingsCompressTab.
/// </summary>
internal static class VideoFilePickerTypes
{
    /// <summary>Broad open/import filter — anything the in-process decoder can read.</summary>
    public static FilePickerFileType OpenVideos { get; } = new("Video Files")
    {
        Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.wmv", "*.flv" }
    };

    /// <summary>Save filter covering every container the app can produce.</summary>
    public static FilePickerFileType SaveVideos { get; } = new("Video Files")
    {
        Patterns = new[] { "*.mp4", "*.mkv", "*.gif", "*.webm", "*.mov" }
    };

    public static FilePickerFileType AllFiles { get; } = new("All Files") { Patterns = new[] { "*.*" } };

    public static FilePickerFileType PngImage { get; } = new("PNG Image") { Patterns = new[] { "*.png" } };
    public static FilePickerFileType JpegImage { get; } = new("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } };
    public static FilePickerFileType WebpImage { get; } = new("WebP Image") { Patterns = new[] { "*.webp" } };

    private static readonly (string Ext, FilePickerFileType Type)[] SaveImageFormats =
    {
        ("png", PngImage),
        ("jpg", JpegImage),
        ("webp", WebpImage),
    };

    private static readonly (string Ext, FilePickerFileType Type)[] SaveFormats =
    {
        ("mp4", new FilePickerFileType("MP4 Video") { Patterns = new[] { "*.mp4" } }),
        ("mkv", new FilePickerFileType("MKV Video") { Patterns = new[] { "*.mkv" } }),
        ("mov", new FilePickerFileType("MOV Video") { Patterns = new[] { "*.mov" } }),
        ("gif", new FilePickerFileType("GIF") { Patterns = new[] { "*.gif" } }),
        ("webm", new FilePickerFileType("WebM Video") { Patterns = new[] { "*.webm" } }),
    };

    /// <summary>
    /// Per-format save choices with the source's own format first (so the dialog defaults to it) and
    /// "All Files" last. Unknown/empty extensions default to mp4.
    /// </summary>
    public static (List<FilePickerFileType> Choices, string DefaultExt) SaveChoicesPreferring(string? sourceExt)
    {
        string ext = (sourceExt ?? string.Empty).TrimStart('.').ToLowerInvariant();
        bool known = false;
        foreach (var (fmt, _) in SaveFormats)
        {
            if (fmt == ext)
            {
                known = true;
                break;
            }
        }

        string defaultExt = known ? ext : "mp4";
        var choices = new List<FilePickerFileType>(SaveFormats.Length + 1);
        foreach (var (fmt, type) in SaveFormats)
        {
            if (fmt == defaultExt)
            {
                choices.Insert(0, type);
            }
            else
            {
                choices.Add(type);
            }
        }

        choices.Add(AllFiles);
        return (choices, defaultExt);
    }

    /// <summary>
    /// Per-format image save choices with the preferred format first (so the dialog defaults to it) and
    /// "All Files" last. Unknown/empty inputs default to png. Mirrors <see cref="SaveChoicesPreferring"/>.
    /// </summary>
    public static (List<FilePickerFileType> Choices, string DefaultExt) SaveImageChoicesPreferring(string? preferred)
    {
        string ext = (preferred ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg")
        {
            ext = "jpg";
        }

        bool known = false;
        foreach (var (fmt, _) in SaveImageFormats)
        {
            if (fmt == ext)
            {
                known = true;
                break;
            }
        }

        string defaultExt = known ? ext : "png";
        var choices = new List<FilePickerFileType>(SaveImageFormats.Length + 1);
        foreach (var (fmt, type) in SaveImageFormats)
        {
            if (fmt == defaultExt)
            {
                choices.Insert(0, type);
            }
            else
            {
                choices.Add(type);
            }
        }

        choices.Add(AllFiles);
        return (choices, defaultExt);
    }
}
