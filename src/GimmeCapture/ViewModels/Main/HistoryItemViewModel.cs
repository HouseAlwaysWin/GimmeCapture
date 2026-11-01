using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Display wrapper for a <see cref="CaptureHistoryItem"/> in the history panel grid. Its thumbnail is decoded off the UI
/// thread while a card shows the item (<see cref="ShowThumbnail"/>, <see cref="HideThumbnail"/>); the card shows a
/// placeholder until then. Every entry's thumbnail used to be decoded when the panel first opened and kept for the rest
/// of the run: with the history full, 300 entries, 87-90 MB that leaving the tab never gave back. It also carries the
/// per-item action commands (open/copy/reveal/remove) so the data template binds directly to itself.
/// </summary>
public sealed class HistoryItemViewModel : ViewModelBase, IDisposable
{
    // A screenful of cards loads as the panel opens or scrolls: a couple at a time keeps the pool free for everything else.
    private static readonly SemaphoreSlim ThumbnailDecodeGate = new(2);

    private readonly string? _thumbnailPath;
    private readonly Func<string, IImage?> _decodeThumbnail;
    private IImage? _thumbnail;
    private int _viewers;
    private bool _loading;
    private bool _disposed;

    public HistoryItemViewModel(
        CaptureHistoryItem item,
        ICommand openCommand,
        ICommand copyCommand,
        ICommand revealCommand,
        ICommand removeCommand,
        Func<string, IImage?>? decodeThumbnail = null)
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
        _decodeThumbnail = decodeThumbnail ?? DecodeThumbnail;
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
    public IImage? Thumbnail
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
    /// A card showing this item is on screen: the first decodes the thumbnail, off the UI thread, and it is kept while
    /// any card shows the item. Call on the UI thread, once for each <see cref="HideThumbnail"/> that will follow.
    /// </summary>
    internal void ShowThumbnail()
    {
        _viewers++;
        if (_viewers == 1 && _thumbnail == null && !_loading)
        {
            LoadThumbnailAsync().Forget("History.Thumbnail");
        }
    }

    /// <summary>A card showing this item has left the screen, or the panel has: the last lets the thumbnail go.</summary>
    internal void HideThumbnail()
    {
        if (_viewers == 0)
        {
            return;
        }

        _viewers--;
        if (_viewers == 0)
        {
            ReleaseThumbnail();
        }
    }

    // On the UI thread; the decode waits its turn on the pool. A card scrolled away before then decodes nothing, and one
    // that left while it decoded throws the result away.
    private async Task LoadThumbnailAsync()
    {
        if (_disposed || string.IsNullOrEmpty(_thumbnailPath))
        {
            return;
        }

        _loading = true;
        IImage? decoded = null;
        try
        {
            await ThumbnailDecodeGate.WaitAsync();
            try
            {
                if (_viewers > 0 && !_disposed)
                {
                    string path = _thumbnailPath;
                    decoded = await Task.Run(() => _decodeThumbnail(path));
                }
            }
            finally
            {
                ThumbnailDecodeGate.Release();
            }
        }
        finally
        {
            _loading = false;
        }

        if (decoded == null)
        {
            return;
        }

        if (_disposed || _viewers == 0 || _thumbnail != null)
        {
            (decoded as IDisposable)?.Dispose();
            return;
        }

        Thumbnail = decoded;
    }

    private void ReleaseThumbnail()
    {
        var thumbnail = _thumbnail;
        if (thumbnail == null)
        {
            return;
        }

        Thumbnail = null;
        (thumbnail as IDisposable)?.Dispose();
    }

    private static IImage? DecodeThumbnail(string path)
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
        ReleaseThumbnail();
    }
}
