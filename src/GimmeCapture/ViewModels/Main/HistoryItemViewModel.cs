using System;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Display wrapper for a <see cref="CaptureHistoryItem"/> in the history panel grid.
/// Loads the on-disk PNG thumbnail (if present) into an Avalonia bitmap for binding, and carries the
/// per-item action commands (open/copy/reveal/remove) so the data template binds directly to itself.
/// </summary>
public sealed class HistoryItemViewModel : ViewModelBase, IDisposable
{
    public HistoryItemViewModel(
        CaptureHistoryItem item,
        ICommand openCommand,
        ICommand copyCommand,
        ICommand revealCommand,
        ICommand removeCommand)
    {
        Id = item.Id;
        FilePath = item.FilePath;
        FileName = item.FileName;
        Source = item.Source;
        IsVideo = item.Kind == CaptureHistoryKind.Video;
        IsImage = !IsVideo;
        CapturedAtLocal = item.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Dimensions = item.Width > 0 && item.Height > 0 ? $"{item.Width}×{item.Height}" : string.Empty;

        OpenCommand = openCommand;
        CopyCommand = copyCommand;
        RevealCommand = revealCommand;
        RemoveCommand = removeCommand;

        if (!string.IsNullOrEmpty(item.ThumbnailPath) && System.IO.File.Exists(item.ThumbnailPath))
        {
            try { Thumbnail = new Bitmap(item.ThumbnailPath); }
            catch (Exception ex) { AppLog.Warning("HistoryItem.LoadThumbnail", ex); }
        }
    }

    public string Id { get; }
    public string FilePath { get; }
    public string FileName { get; }
    public CaptureHistorySource Source { get; }
    public bool IsVideo { get; }
    public bool IsImage { get; }
    public string CapturedAtLocal { get; }
    public string Dimensions { get; }
    public Bitmap? Thumbnail { get; }
    public bool HasThumbnail => Thumbnail != null;

    public ICommand OpenCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand RemoveCommand { get; }

    public void Dispose() => Thumbnail?.Dispose();
}
