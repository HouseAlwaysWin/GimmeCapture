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

namespace GimmeCapture.Tests;

public class FloatingImageViewModelTests
{
    class MockClipboardService : IClipboardService
    {
        public Bitmap? CopiedImage { get; private set; }
        public bool CopyCalled { get; private set; }
        
        public Task CopyImageAsync(Bitmap bitmap)
        {
            CopyCalled = true;
            CopiedImage = bitmap;
            return Task.CompletedTask;
        }

        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task CopyFileAsync(string filePath) => Task.CompletedTask;
    }

    [Fact]
    public async Task CopyCommand_ShouldNotCall_ClipboardService_WhenImageIsNull()
    {
        // Arrange
        var mockService = new MockClipboardService();
        var mockSettings = new Mock<AppSettingsService>();
        var mockAiPath = new Mock<AIPathService>(mockSettings.Object);
        var mockAiResolver = new Mock<NativeResolverService>(mockAiPath.Object);
        var mockAiDownloader = new Mock<AIModelDownloader>();
        var mockAi = new Mock<AIResourceService>(mockSettings.Object, mockAiPath.Object, mockAiResolver.Object, mockAiDownloader.Object);
        var vm = new FloatingImageViewModel(null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, mockService, mockAi.Object, mockSettings.Object, mockAiPath.Object);

        // Act
        await vm.CopyCommand.Execute();

        // Assert
        Assert.False(mockService.CopyCalled);
    }
}
