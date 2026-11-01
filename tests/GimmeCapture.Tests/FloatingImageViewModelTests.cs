using GimmeCapture.Models;
using GimmeCapture.ViewModels;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Services;
using GimmeCapture.Services.Abstractions;
using Avalonia.Media.Imaging;
using Avalonia;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using GimmeCapture.Services.Core.AI;

namespace GimmeCapture.Tests;

// In the renderer's collection for the test that rotates a real picture.
[Collection(AvaloniaRendererCollection.Name)]
public class FloatingImageViewModelTests
{
    class MockClipboardService : IClipboardService
    {
        public Bitmap? CopiedImage { get; private set; }
        public bool CopyCalled { get; private set; }
        
        public Task<bool> CopyImageAsync(Bitmap bitmap)
        {
            CopyCalled = true;
            CopiedImage = bitmap;
            return Task.FromResult(true);
        }

        public Task<bool> CopyTextAsync(string text) => Task.FromResult(true);
        public Task<bool> CopyFileAsync(string filePath) => Task.FromResult(true);
        public Task<bool> CopyFileAndImageAsync(string filePath, Avalonia.Media.Imaging.Bitmap bitmap) => Task.FromResult(true);
    }

    [Fact]
    public async Task CopyCommand_ShouldNotCall_ClipboardService_WhenImageIsNull()
    {
        // Arrange
        var mockService = new MockClipboardService();
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());
        
        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiDownloader.Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());

        // Act
        await vm.CopyCommand.Execute();

        // Assert
        Assert.False(mockService.CopyCalled);
    }

    [Fact]
    public async Task ImageChange_ShouldPreserveSam2Service_AndInvalidatePreparedImage()
    {
        var mockService = new MockClipboardService();
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());

        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiDownloader.Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());
        // A real SAM2Service, with a stand-in for the model.
        var sam2Service = StandInEncoderSam2Service.Create();
        using (var pinPixels = new SkiaSharp.SKBitmap(8, 8))
        {
            await sam2Service.EnsureImagePreparedAsync(pinPixels);
        }

        Assert.True(sam2Service.IsImagePreparedForTesting);
        vm.SetSam2ServiceForTesting(sam2Service);

        vm.HandleImageChangedForSam2();

        Assert.Same(sam2Service, vm.CurrentSam2ServiceForTesting);
        Assert.False(sam2Service.IsImagePreparedForTesting);
        Assert.Null(sam2Service.PreparedImageKeyForTesting);
    }

    [Fact]
    public void ReleaseSam2Resources_ShouldDisposeService_AndReleaseRuntimeLease()
    {
        var mockService = new MockClipboardService();
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());

        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiDownloader.Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());
        var sam2Service = new SAM2Service(sam2Runtime, mockSettings.Object);

        vm.SetSam2ServiceForTesting(sam2Service);
        vm.EnsureSam2Lease();
        Assert.True(sam2Runtime.HasActiveLeases);

        vm.ReleaseSam2Resources();

        Assert.Null(vm.CurrentSam2ServiceForTesting);
        Assert.False(sam2Runtime.HasActiveLeases);
    }

    [Fact]
    public async Task Saving_LeavesThePinsPictureAlone_CancelledOrNot()
    {
        // The save swapped a copy with the annotations drawn in for the pin's picture while its dialog was open: the pin
        // drew them twice, and a new picture drops smart selection's prepared one and its points.
        var vm = CreatePin(AvaloniaRenderer.Create(40, 30, static (_, _) => 0xFFFFFFFFu));
        var picture = vm.Image;
        vm.Annotations.Add(new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(20, 20),
            IsFilled = true,
            Color = Avalonia.Media.Colors.Red
        });
        int pictureChanges = 0;
        vm.PropertyChanged += (_, e) => pictureChanges += e.PropertyName == nameof(vm.Image) ? 1 : 0;
        vm.SaveAction = () => Task.CompletedTask; // the dialog cancelled

        await vm.SaveCommand.Execute();

        Assert.Equal(0, pictureChanges);
        Assert.Same(picture, vm.Image);
    }

    [Fact]
    public async Task WhatASaveWrites_HasTheAnnotationsDrawnIn()
    {
        var vm = CreatePin(AvaloniaRenderer.Create(40, 30, static (_, _) => 0xFFFFFFFFu));
        vm.Annotations.Add(new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(20, 20),
            IsFilled = true,
            Color = Avalonia.Media.Colors.Red
        });

        var (encoded, bytes, _) = await vm.EncodeForSaveAsync(SkiaSharp.SKEncodedImageFormat.Png, 100);

        Assert.True(encoded);
        using var saved = SkiaSharp.SKBitmap.Decode(bytes);
        Assert.Equal(new SkiaSharp.SKColor(255, 0, 0, 255), saved.GetPixel(15, 15));
        Assert.Equal(SkiaSharp.SKColors.White, saved.GetPixel(2, 2));
        // The pin's own picture stays as it was.
        Assert.Equal(0xFFFFFFFFu, AvaloniaRenderer.ReadPixels(vm.Image!)[(15 * 40) + 15]);
    }

    private static FloatingImageViewModel CreatePin(Bitmap picture)
    {
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());
        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, new Mock<AIModelDownloader>().Object);
        return new FloatingImageViewModel(
            picture, picture.PixelSize.Width, picture.PixelSize.Height, Avalonia.Media.Colors.Red, 2.0, false, false,
            new MockClipboardService(), mockAi.Object, new SAM2RuntimeService(mockAiPath.Object), mockSettings.Object,
            mockAiPath.Object, new ResourceQueueService());
    }

    [Fact]
    public async Task Dispose_LetsGoOfThePicturesKeptForUndo_AndOfSmartSelection()
    {
        // A pin's window disposes its view model however it closes. Closed by Alt+F4 it did not, and every picture in the
        // pin's undo history stayed in BitmapHistoryAction's table until the app exited, with smart selection's lease.
        AvaloniaRenderer.EnsureRegistered();
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());
        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, new Mock<AIModelDownloader>().Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object);
        var original = AvaloniaRenderer.Create(8, 6, static (x, y) => 0xFF000000u | ((uint)(x * 30) << 16) | (uint)(y * 40));
        var vm = new FloatingImageViewModel(original, 8, 6, Avalonia.Media.Colors.Red, 2.0, false, false, new MockClipboardService(), mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());
        await vm.RotateRightCommand.Execute();
        var rotated = vm.Image;
        Assert.NotSame(original, rotated);
        vm.EnsureSam2Lease();

        vm.Dispose();

        Assert.Throws<ObjectDisposedException>(() => original.PixelSize);
        // On screen until the window goes, so the history leaves it to the collector.
        Assert.Equal(new PixelSize(6, 8), rotated!.PixelSize);
        Assert.False(sam2Runtime.HasActiveLeases);
    }
}
