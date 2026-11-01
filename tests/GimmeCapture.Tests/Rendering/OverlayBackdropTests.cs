using Avalonia.Media.Imaging;
using GimmeCapture.Services.Abstractions;
using SkiaSharp;

namespace GimmeCapture.Tests.Rendering;

/// <summary>
/// The frozen overlay's backdrop with the real display conversion, which needs Avalonia's renderer, hence the collection.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public class OverlayBackdropTests
{
    [Fact]
    public void TheStill_IsReadFromTheShownCopy_SoTheDesktopIsHeldOnce()
    {
        // The backdrop kept the grab beside the copy it shows: 18 MB each for a desktop of 3968x1160.
        AvaloniaRenderer.EnsureRegistered();
        var grab = new SKBitmap(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                grab.SetPixel(x, y, new SKColor((byte)(x * 3), (byte)(y * 5), (byte)(x + y), 255));
            }
        }

        using var backdrop = OverlayBackdrop.TryCreate(grab, 1.25, OverlayBackdrop.DefaultToDisplay)!;

        var shown = Assert.IsAssignableFrom<WriteableBitmap>(backdrop.Image);
        using (var locked = shown.Lock())
        {
            Assert.Equal(locked.Address, backdrop.Pixels.GetPixels());
        }

        Assert.Equal(IntPtr.Zero, grab.Handle);
        Assert.Equal(new SKColor(30, 35, 17, 255), backdrop.Pixels.GetPixel(10, 7));
        Assert.Equal(1.25, backdrop.Scaling);
    }
}
