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
using System.Reflection;

namespace GimmeCapture.Tests;

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
        var mockAiResolver = new Mock<NativeResolverService>(mockAiPath.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiResolver.Object, mockAiDownloader.Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object, mockAiResolver.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());

        // Act
        await vm.CopyCommand.Execute();

        // Assert
        Assert.False(mockService.CopyCalled);
    }

    [Fact]
    public void ImageChange_ShouldPreserveSam2Service_AndInvalidatePreparedImage()
    {
        var mockService = new MockClipboardService();
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());

        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAiResolver = new Mock<NativeResolverService>(mockAiPath.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiResolver.Object, mockAiDownloader.Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object, mockAiResolver.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());
        var sam2Service = new SAM2Service(sam2Runtime, mockSettings.Object);

        typeof(SAM2Service).GetField("_isImagePrepared", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(sam2Service, true);
        typeof(SAM2Service).GetField("_preparedImageKey", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(sam2Service, "prepared");
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
        var mockAiResolver = new Mock<NativeResolverService>(mockAiPath.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiResolver.Object, mockAiDownloader.Object);
        var sam2Runtime = new SAM2RuntimeService(mockAiPath.Object, mockAiResolver.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, sam2Runtime, mockSettings.Object, mockAiPath.Object, new ResourceQueueService());
        var sam2Service = new SAM2Service(sam2Runtime, mockSettings.Object);

        vm.SetSam2ServiceForTesting(sam2Service);
        vm.EnsureSam2Lease();
        Assert.True(sam2Runtime.HasActiveLeases);

        vm.ReleaseSam2Resources();

        Assert.Null(vm.CurrentSam2ServiceForTesting);
        Assert.False(sam2Runtime.HasActiveLeases);
    }
}
