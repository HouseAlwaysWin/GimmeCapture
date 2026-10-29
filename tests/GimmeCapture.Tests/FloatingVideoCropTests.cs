using Avalonia;
using GimmeCapture.ViewModels.Floating;

namespace GimmeCapture.Tests;

public class FloatingVideoCropTests
{
    [Fact]
    public void SelectionToPixelCropRect_IdentityScale_ReturnsSelection()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(10, 20, 100, 50), displayWidth: 200, displayHeight: 200, originalWidth: 200, originalHeight: 200);

        Assert.Equal(10, crop.X);
        Assert.Equal(20, crop.Y);
        Assert.Equal(100, crop.Width);
        Assert.Equal(50, crop.Height);
    }

    [Fact]
    public void SelectionToPixelCropRect_ScalesDisplayToVideoPixels()
    {
        // Display is half the video size -> scale factor 2.
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(10, 10, 40, 30), displayWidth: 100, displayHeight: 100, originalWidth: 200, originalHeight: 200);

        Assert.Equal(20, crop.X);
        Assert.Equal(20, crop.Y);
        Assert.Equal(80, crop.Width);
        Assert.Equal(60, crop.Height);
    }

    [Fact]
    public void SelectionToPixelCropRect_RoundsWidthAndHeightDownToEven()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(0, 0, 51, 51), displayWidth: 200, displayHeight: 200, originalWidth: 200, originalHeight: 200);

        Assert.Equal(50, crop.Width);
        Assert.Equal(50, crop.Height);
    }

    [Fact]
    public void SelectionToPixelCropRect_ClampsToVideoBounds()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(190, 190, 100, 100), displayWidth: 200, displayHeight: 200, originalWidth: 200, originalHeight: 200);

        Assert.True(crop.X + crop.Width <= 200);
        Assert.True(crop.Y + crop.Height <= 200);
        Assert.True(crop.Width >= 2);
        Assert.True(crop.Height >= 2);
    }

    [Fact]
    public void SelectionToPixelCropRect_DegenerateVideo_ReturnsEmpty()
    {
        var crop = FloatingVideoViewModel.SelectionToPixelCropRect(
            new Rect(0, 0, 10, 10), displayWidth: 1, displayHeight: 1, originalWidth: 1, originalHeight: 1);

        Assert.Equal(0, crop.Width);
        Assert.Equal(0, crop.Height);
    }

    [Fact]
    public void MoveExportToPinTempFile_MovesTheClipIntoThePinFolder_AndDropsTheExportFolder()
    {
        // A crop copied its export into the pin folder on the UI thread, so the clip sat in the temp folder twice and
        // its GimmeCapture_Export_* folder stayed until a start-up sweep half a day later.
        string exportFolder = Path.Combine(Path.GetTempPath(), "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportFolder);
        string exported = Path.Combine(exportFolder, "output.mp4");
        File.WriteAllBytes(exported, [1, 2, 3]);
        string? pinned = null;
        try
        {
            pinned = FloatingVideoViewModel.MoveExportToPinTempFile(exported);

            Assert.True(PinTempDirectory.IsOwnedTempFile(pinned));
            Assert.StartsWith("crop_", Path.GetFileName(pinned));
            Assert.Equal(".mp4", Path.GetExtension(pinned));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(pinned));
            Assert.False(Directory.Exists(exportFolder));
        }
        finally
        {
            PinTempDirectory.TryDeleteOwnedFile(pinned);
            if (Directory.Exists(exportFolder))
            {
                Directory.Delete(exportFolder, recursive: true);
            }
        }
    }

    [Fact]
    public void TryDeleteExportFolder_LeavesAFolderThatIsNotAnExportsAlone()
    {
        // The pin's own clip is no export: saving it unedited must not take its folder with it.
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "clip.mp4");
        File.WriteAllBytes(clip, [1]);
        try
        {
            FloatingVideoViewModel.TryDeleteExportFolder(clip);

            Assert.True(File.Exists(clip));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
