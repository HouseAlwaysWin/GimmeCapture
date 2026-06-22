using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class CaptureHistoryServiceTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(CaptureHistoryServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WritePng(string dir, string name, int w = 40, int h = 30)
    {
        var path = Path.Combine(dir, name);
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public async Task AddImage_IndexesItem_WithThumbnailAndDimensions()
    {
        var tempDir = NewTempDir();
        var settings = new AppSettingsService(tempDir);
        var service = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        var imagePath = WritePng(tempDir, "shot.png", 120, 80);

        await service.AddImageAsync(imagePath);

        var items = await service.GetItemsAsync();
        var item = Assert.Single(items);
        Assert.Equal(imagePath, item.FilePath);
        Assert.Equal(CaptureHistoryKind.Image, item.Kind);
        Assert.Equal(120, item.Width);
        Assert.Equal(80, item.Height);
        Assert.NotNull(item.ThumbnailPath);
        Assert.True(File.Exists(item.ThumbnailPath!));
    }

    [Fact]
    public async Task AddImage_Persists_AcrossServiceInstances()
    {
        var tempDir = NewTempDir();
        var settings = new AppSettingsService(tempDir);
        var imagePath = WritePng(tempDir, "shot.png");

        var service1 = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        await service1.AddImageAsync(imagePath);

        var service2 = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        var items = await service2.GetItemsAsync();

        Assert.Single(items);
    }

    [Fact]
    public async Task AddImage_WhenHistoryDisabled_DoesNothing()
    {
        var tempDir = NewTempDir();
        var settings = new AppSettingsService(tempDir);
        settings.Settings.EnableHistory = false;
        var service = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        var imagePath = WritePng(tempDir, "shot.png");

        await service.AddImageAsync(imagePath);

        Assert.Empty(await service.GetItemsAsync());
    }

    [Fact]
    public async Task Remove_DropsItem_AndDeletesThumbnail_ButKeepsSourceByDefault()
    {
        var tempDir = NewTempDir();
        var settings = new AppSettingsService(tempDir);
        var service = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        var imagePath = WritePng(tempDir, "shot.png");
        await service.AddImageAsync(imagePath);
        var item = Assert.Single(await service.GetItemsAsync());

        await service.RemoveAsync(item.Id);

        Assert.Empty(await service.GetItemsAsync());
        Assert.False(File.Exists(item.ThumbnailPath!), "Thumbnail should be deleted.");
        Assert.True(File.Exists(imagePath), "Source file should be kept by default.");
    }

    [Fact]
    public async Task Load_DropsEntries_WhoseSourceFileWasDeleted()
    {
        var tempDir = NewTempDir();
        var settings = new AppSettingsService(tempDir);
        var imagePath = WritePng(tempDir, "shot.png");

        var service1 = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        await service1.AddImageAsync(imagePath);

        File.Delete(imagePath);

        var service2 = new CaptureHistoryService(settings, rootDirectoryOverride: tempDir);
        Assert.Empty(await service2.GetItemsAsync());
    }
}
