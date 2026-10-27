using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Display wrapper for a <see cref="CaptureHistoryItem"/> in the history panel grid. Its thumbnail is decoded off
/// the UI thread by <see cref="LoadThumbnailAsync"/>; the card shows a placeholder until then. It also carries the
/// per-item action commands (open/copy/reveal/remove) so the data template binds directly to itself.
/// </summary>
public sealed class HistoryItemViewModel : ViewModelBase, IDisposable
{
    // Up to 300 thumbnails load when the panel opens: a couple at a time keeps the pool free for everything else.
    private static readonly SemaphoreSlim ThumbnailDecodeGate = new(2);

    private readonly string? _thumbnailPath;
    private Bitmap? _thumbnail;
    private bool _disposed;

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
        CapturedAtUtc = item.CapturedAtUtc;
        CapturedAtLocal = item.CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Dimensions = item.Width > 0 && item.Height > 0 ? $"{item.Width}×{item.Height}" : string.Empty;

        OpenCommand = openCommand;
        CopyCommand = copyCommand;
        RevealCommand = revealCommand;
        RemoveCommand = removeCommand;

        _thumbnailPath = item.ThumbnailPath;
    }

    public string Id { get; }
    public string FilePath { get; }
    public string FileName { get; }
    public CaptureHistorySource Source { get; }
    public bool IsVideo { get; }
    public bool IsImage { get; }
    /// <summary>Raw capture time, kept alongside the formatted string so sorting compares dates, not text.</summary>
    public DateTime CapturedAtUtc { get; }
    public string CapturedAtLocal { get; }
    public string Dimensions { get; }
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            this.RaiseAndSetIfChanged(ref _thumbnail, value);
            this.RaisePropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => Thumbnail != null;

    public ICommand OpenCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Decodes the thumbnail off the UI thread. Call it from the UI thread: the result is applied there, or thrown
    /// away when the item was disposed while it decoded.
    /// </summary>
    internal async Task LoadThumbnailAsync()
    {
        if (_disposed || _thumbnail != null || string.IsNullOrEmpty(_thumbnailPath))
        {
            return;
        }

        Bitmap? decoded;
        await ThumbnailDecodeGate.WaitAsync();
        try
        {
            decoded = await Task.Run(() => DecodeThumbnail(_thumbnailPath));
        }
        finally
        {
            ThumbnailDecodeGate.Release();
        }

        if (decoded == null)
        {
            return;
        }

        if (_disposed)
        {
            decoded.Dispose();
            return;
        }

        Thumbnail = decoded;
    }

    private static Bitmap? DecodeThumbnail(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            AppLog.Warning("HistoryItem.LoadThumbnail", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _thumbnail?.Dispose();
    }
}
