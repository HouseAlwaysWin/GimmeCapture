using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Floating;
using Moq;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// A video pin's decode thread writes each frame into the pin's bitmap itself, under the bitmap's lock, which Avalonia's
/// renderer takes while it draws it; the UI thread only redraws. It used to copy each frame into the bitmap from a
/// buffer the decoder had copied it into, most of the UI thread's work while a 1450x900 pin played (PinPlaybackProbeTests).
/// The view model is built without playing anything: playback starts on the UI thread, which these tests do not run.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public sealed class FloatingVideoFrameTests
{
    private const int Width = 64;
    private const int Height = 32;

    [Fact]
    public void AFrameHandedOver_IsInTheBitmapAtOnce()
    {
        using var vm = NewPin();
        byte[] frame = Frame(Width, Height, (x, y) => 0xFF000000u | (uint)(x << 16) | (uint)(y << 8) | 0x40u);

        vm.UpdateBitmap(frame, generation: 0);

        uint[] pixels = AvaloniaRenderer.ReadPixels(vm.VideoBitmap!);
        Assert.Equal(0xFF000040u, pixels[0]);
        Assert.Equal(0xFF3F1F40u, pixels[(Height - 1) * Width + (Width - 1)]);
        Assert.Equal(0xFF050340u, pixels[(3 * Width) + 5]);
    }

    // A frame of another size comes from before a crop or a resize, which the bitmap no longer is; and one from an
    // earlier playback is one the pin has moved on from (a seek, a new piece of a cut).
    [Theory]
    [InlineData(Width / 2, Height / 2, 0)]
    [InlineData(Width, Height, 1)]
    public void AFrameThePinHasMovedOnFrom_LeavesTheBitmapAlone(int width, int height, int generation)
    {
        using var vm = NewPin();
        byte[] frame = Frame(width, height, (_, _) => 0xFFFFFFFFu);

        vm.UpdateBitmap(frame, generation);

        Assert.All(AvaloniaRenderer.ReadPixels(vm.VideoBitmap!), pixel => Assert.NotEqual(0xFFFFFFFFu, pixel));
    }

    private static FloatingVideoViewModel NewPin()
    {
        AvaloniaRenderer.EnsureRegistered();
        return new FloatingVideoViewModel(
            "pinned-recording.mp4", Width, Height, Width, Height, Colors.Red, 2.0, false, false, Mock.Of<IClipboardService>(), null);
    }

    // A packed BGRA frame, as the player hands one over, with `pixel`(x, y) as each pixel's word in memory.
    private static byte[] Frame(int width, int height, Func<int, int, uint> pixel)
    {
        var frame = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BitConverter.TryWriteBytes(frame.AsSpan(((y * width) + x) * 4), pixel(x, y));
            }
        }

        return frame;
    }
}
