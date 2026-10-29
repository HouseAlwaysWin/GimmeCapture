using SkiaSharp;

namespace GimmeCapture.Tests;

// SAM2Service prepares an image for clicks by running the encoder on it, about a second on a CPU, and meanwhile the pin
// can change (a crop, an undo), the model can be switched, or the pin can let go of the service (Esc, another tool).
// Each of those invalidates the image. A prepare still running then marked its image prepared anyway, with the size the
// invalidation had zeroed: the next click scaled its points by that zero and found nothing, and a pin changed back to
// the image being prepared was never prepared again. These hold the encoder open with a stand-in for the model.
public sealed class Sam2ServicePrepareTests
{
    // A regression fails rather than hangs.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task APrepare_InvalidatedWhileTheEncoderRuns_LeavesNothingPrepared()
    {
        var sam2 = StandInEncoderSam2Service.Create();
        sam2.HoldEncodes = true;
        using var image = Image(64, 48);

        Task prepare = sam2.EnsureImagePreparedAsync(image);
        StandInEncode encode = await sam2.NextEncodeAsync().WaitAsync(Timeout);
        sam2.InvalidatePreparedImage();
        encode.Finish();
        await prepare.WaitAsync(Timeout);

        Assert.False(sam2.IsImagePrepared, $"left prepared after the invalidation, at a size of {sam2.PreparedImageSizeForTesting}");
    }

    [Fact]
    public async Task APin_ChangedBackWhileItsImageWasBeingPrepared_IsPreparedAgain()
    {
        var sam2 = StandInEncoderSam2Service.Create();
        sam2.HoldEncodes = true;
        using var image = Image(64, 48);

        Task prepare = sam2.EnsureImagePreparedAsync(image);
        StandInEncode encode = await sam2.NextEncodeAsync().WaitAsync(Timeout);
        // A crop and its undo: the pin is this image again.
        sam2.InvalidatePreparedImage();
        sam2.InvalidatePreparedImage();
        encode.Finish();
        await prepare.WaitAsync(Timeout);

        sam2.HoldEncodes = false;
        await sam2.EnsureImagePreparedAsync(image).WaitAsync(Timeout);

        Assert.Equal(2, sam2.EncodeCount);
        Assert.True(sam2.IsImagePrepared);
        Assert.Equal((64, 48), sam2.PreparedImageSizeForTesting);
    }

    [Fact]
    public async Task APrepare_QueuedBehindAnInvalidatedOne_PreparesItsOwnImage()
    {
        var sam2 = StandInEncoderSam2Service.Create();
        sam2.HoldEncodes = true;
        using var first = Image(64, 48);
        using var second = Image(32, 24);

        Task firstPrepare = sam2.EnsureImagePreparedAsync(first);
        StandInEncode firstEncode = await sam2.NextEncodeAsync().WaitAsync(Timeout);
        sam2.InvalidatePreparedImage();
        Task secondPrepare = sam2.EnsureImagePreparedAsync(second);
        firstEncode.Finish();
        (await sam2.NextEncodeAsync().WaitAsync(Timeout)).Finish();
        await Task.WhenAll(firstPrepare, secondPrepare).WaitAsync(Timeout);

        Assert.True(sam2.IsImagePrepared);
        Assert.Equal((32, 24), sam2.PreparedImageSizeForTesting);
    }

    [Fact]
    public async Task APrepare_RunningWhenThePinLetsGoOfTheService_LeavesNothingPrepared()
    {
        var sam2 = StandInEncoderSam2Service.Create();
        sam2.HoldEncodes = true;
        using var image = Image(64, 48);

        Task prepare = sam2.EnsureImagePreparedAsync(image);
        StandInEncode encode = await sam2.NextEncodeAsync().WaitAsync(Timeout);
        sam2.Dispose();
        encode.Finish();
        await prepare.WaitAsync(Timeout);

        Assert.False(sam2.IsImagePrepared, "a service the pin let go of was left prepared");
    }

    [Fact]
    public async Task AServiceThePinLetGoOf_PreparesNothing()
    {
        // A click that was still waiting to prepare the pin when the tool was left. Loading the model for it would leave
        // the model loaded with no lease left to unload it.
        var sam2 = StandInEncoderSam2Service.Create();
        using var image = Image(64, 48);
        sam2.Dispose();

        await sam2.EnsureImagePreparedAsync(image).WaitAsync(Timeout);

        Assert.Equal(0, sam2.LoadCount);
        Assert.Equal(0, sam2.EncodeCount);
        Assert.False(sam2.IsImagePrepared);
    }

    private static SKBitmap Image(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(16, 32, 48));
        return bitmap;
    }
}
