using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Platforms.Windows;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>What <see cref="WindowsScreenCaptureService"/> does with files.</summary>
public sealed class WindowsScreenCaptureServiceTests
{
    [Fact]
    public async Task SaveToFileAsync_OverALargerFile_LeavesOnlyTheNewPicture()
    {
        // The file was opened with OpenWrite, which keeps an existing file's length: a capture saved over a larger
        // file kept that file's tail after its PNG.
        string path = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllBytes(path, new byte[1 << 16]);
            using var picture = new SKBitmap(16, 16);
            picture.Erase(SKColors.Red);
            using var expected = picture.Encode(SKEncodedImageFormat.Png, 100);

            await new WindowsScreenCaptureService(new Mock<IWindowManager>().Object).SaveToFileAsync(picture, path);

            Assert.Equal(expected.ToArray(), File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
