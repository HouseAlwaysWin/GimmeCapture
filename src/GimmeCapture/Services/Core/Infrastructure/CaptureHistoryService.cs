using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Maintains a JSON-backed index of captures that were persisted to disk (saved screenshots and
/// finalized recordings), plus a small PNG thumbnail per item. Honors <see cref="AppSettings.EnableHistory"/>.
/// </summary>
public sealed class CaptureHistoryService
{
    private const int MaxItems = 300;
    private const int ThumbnailMaxLongSide = 320;

    private readonly AppSettingsService _settingsService;
    private readonly string _historyDir;
    private readonly string _thumbsDir;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private List<CaptureHistoryItem>? _items;

    public CaptureHistoryService(AppSettingsService settingsService, string? rootDirectoryOverride = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        var root = string.IsNullOrWhiteSpace(rootDirectoryOverride)
            ? AppStoragePaths.GetRootDirectory()
            : rootDirectoryOverride!;
        _historyDir = Path.Combine(root, "history");
        _thumbsDir = Path.Combine(_historyDir, "thumbs");
        _indexPath = Path.Combine(_historyDir, "history.json");
    }

    private bool IsEnabled => _settingsService.Settings.EnableHistory;

    public async Task AddImageAsync(string filePath, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            int width = 0, height = 0;
            string? thumbPath = GenerateImageThumbnail(filePath, id, ref width, ref height);

            AddItemLocked(new CaptureHistoryItem
            {
                Id = id,
                FilePath = filePath,
                ThumbnailPath = thumbPath,
                Kind = CaptureHistoryKind.Image,
                CapturedAtUtc = DateTime.UtcNow,
                Width = width,
                Height = height
            });
            await SaveIndexLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.AddImage", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddVideoAsync(string filePath, int pixelWidth, int pixelHeight, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            string id = Guid.NewGuid().ToString("N");
            string? thumbPath = await GenerateVideoThumbnailAsync(filePath, id, pixelWidth, pixelHeight, ct).ConfigureAwait(false);

            AddItemLocked(new CaptureHistoryItem
            {
                Id = id,
                FilePath = filePath,
                ThumbnailPath = thumbPath,
                Kind = CaptureHistoryKind.Video,
                CapturedAtUtc = DateTime.UtcNow,
                Width = pixelWidth,
                Height = pixelHeight
            });
            await SaveIndexLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.AddVideo", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CaptureHistoryItem>> GetItemsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            // Newest first.
            return _items!.OrderByDescending(i => i.CapturedAtUtc).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string id, bool deleteSourceFile = false)
    {
        if (string.IsNullOrEmpty(id)) return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            var item = _items!.FirstOrDefault(i => i.Id == id);
            if (item == null) return;

            _items!.Remove(item);
            DeleteAssociatedFiles(item, deleteSourceFile);
            await SaveIndexLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.Remove", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(bool deleteSourceFiles = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
            foreach (var item in _items!)
            {
                DeleteAssociatedFiles(item, deleteSourceFiles);
            }
            _items!.Clear();
            await SaveIndexLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.Clear", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AddItemLocked(CaptureHistoryItem item)
    {
        _items!.Add(item);

        // Retention: keep the newest MaxItems, deleting thumbnails of pruned entries.
        if (_items.Count > MaxItems)
        {
            var pruned = _items
                .OrderByDescending(i => i.CapturedAtUtc)
                .Skip(MaxItems)
                .ToList();
            foreach (var old in pruned)
            {
                _items.Remove(old);
                DeleteThumbnail(old);
            }
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_items != null) return;

        FileLocationService.EnsureDirectory(_thumbsDir, "CaptureHistory.EnsureDirs");

        if (!File.Exists(_indexPath))
        {
            _items = new List<CaptureHistoryItem>();
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(_indexPath).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<List<CaptureHistoryItem>>(json, JsonOptions)
                         ?? new List<CaptureHistoryItem>();
            // Drop entries whose source file no longer exists (deleted outside the app).
            _items = loaded.Where(i => !string.IsNullOrEmpty(i.FilePath) && File.Exists(i.FilePath)).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.Load", ex);
            _items = new List<CaptureHistoryItem>();
        }
    }

    private async Task SaveIndexLockedAsync()
    {
        try
        {
            FileLocationService.EnsureDirectory(_historyDir, "CaptureHistory.EnsureDirs");
            string json = JsonSerializer.Serialize(_items, JsonOptions);
            await File.WriteAllTextAsync(_indexPath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.Save", ex);
        }
    }

    private string? GenerateImageThumbnail(string filePath, string id, ref int width, ref int height)
    {
        try
        {
            using var src = SKBitmap.Decode(filePath);
            if (src == null) return null;

            width = src.Width;
            height = src.Height;
            var (tw, th) = ScaleToThumbnail(src.Width, src.Height);

            using var resized = src.Resize(new SKImageInfo(tw, th), new SKSamplingOptions(SKFilterMode.Linear));
            if (resized == null) return null;

            return EncodeThumbnail(resized, id);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.ImageThumbnail", ex);
            return null;
        }
    }

    private async Task<string?> GenerateVideoThumbnailAsync(string filePath, string id, int pixelWidth, int pixelHeight, CancellationToken ct)
    {
        try
        {
            var (tw, th) = pixelWidth > 0 && pixelHeight > 0
                ? ScaleToThumbnail(pixelWidth, pixelHeight)
                : (ThumbnailMaxLongSide, ThumbnailMaxLongSide * 9 / 16);

            byte[]? firstFrame = null;
            using var player = new LibavVideoFramePlayer();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                await player.PlayAsync(
                    filePath,
                    tw,
                    th,
                    startSeconds: 0,
                    speed: 1.0,
                    loop: false,
                    onFrame: (bytes, _) =>
                    {
                        if (firstFrame == null)
                        {
                            firstFrame = (byte[])bytes.Clone();
                            cts.Cancel();
                        }
                    },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: we cancel after grabbing the first frame.
            }

            if (firstFrame == null) return null;

            var info = new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bmp = new SKBitmap();
            if (!bmp.InstallPixels(info, PinAndGetPointer(firstFrame, out var handle), info.RowBytes,
                    (_, _) => handle.Free(), null))
            {
                handle.Free();
                return null;
            }

            return EncodeThumbnail(bmp, id);
        }
        catch (Exception ex)
        {
            AppLog.Warning("CaptureHistory.VideoThumbnail", ex);
            return null;
        }
    }

    private static IntPtr PinAndGetPointer(byte[] data, out GCHandle handle)
    {
        handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    private string EncodeThumbnail(SKBitmap bitmap, string id)
    {
        FileLocationService.EnsureDirectory(_thumbsDir, "CaptureHistory.EnsureDirs");
        string thumbPath = Path.Combine(_thumbsDir, $"{id}.png");
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(thumbPath, data.ToArray());
        return thumbPath;
    }

    private static (int Width, int Height) ScaleToThumbnail(int width, int height)
    {
        if (width <= 0 || height <= 0) return (ThumbnailMaxLongSide, ThumbnailMaxLongSide);
        if (width <= ThumbnailMaxLongSide && height <= ThumbnailMaxLongSide) return (width, height);

        double scale = (double)ThumbnailMaxLongSide / Math.Max(width, height);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private void DeleteAssociatedFiles(CaptureHistoryItem item, bool deleteSourceFile)
    {
        DeleteThumbnail(item);
        if (deleteSourceFile && !string.IsNullOrEmpty(item.FilePath))
        {
            try { if (File.Exists(item.FilePath)) File.Delete(item.FilePath); }
            catch (Exception ex) { AppLog.Warning("CaptureHistory.DeleteSource", ex); }
        }
    }

    private static void DeleteThumbnail(CaptureHistoryItem item)
    {
        if (string.IsNullOrEmpty(item.ThumbnailPath)) return;
        try { if (File.Exists(item.ThumbnailPath)) File.Delete(item.ThumbnailPath); }
        catch (Exception ex) { AppLog.Warning("CaptureHistory.DeleteThumbnail", ex); }
    }
}
