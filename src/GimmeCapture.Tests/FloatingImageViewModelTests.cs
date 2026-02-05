using Xunit;
using GimmeCapture.ViewModels;
using GimmeCapture.Services;
using Avalonia.Media.Imaging;
using Avalonia;
using System.Threading.Tasks;
using Avalonia.Platform;
using System.Reactive.Linq;

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
        var vm = new FloatingImageViewModel(null!, Avalonia.Media.Colors.Red, 2.0, false, false, mockService);

        // Act
        await vm.CopyCommand.Execute();

        // Assert
        Assert.False(mockService.CopyCalled);
    }
}
