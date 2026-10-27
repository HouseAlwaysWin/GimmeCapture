using System;
using System.IO;
using System.Threading.Tasks;
using SkiaSharp;

namespace GimmeCapture.Tests;

// A recording the user pinned and never saved used to stay in the temp folder for good. The pin owns it now, and
// deletes it with its history entry when the pin closes; one copied to the clipboard as a file stays, and a save moves
// the history entry to the saved file first.
public sealed class UnsavedRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    private string Temp => Path.Combine(_root, "Temp");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // The history makes a thumbnail of what it lists. An image entry stands in for the video one: what the history
    // forgets or moves is the entry for a path, whatever its kind.
    private static string WriteThumbnailable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(40, 30);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string NewRecording() =>
        WriteThumbnailable(Path.Combine(Temp, RecordingTempDirectory.NewRecordingFileName("mp4")));

    private CaptureHistoryService NewHistory() =>
        new(new AppSettingsService(_root), rootDirectoryOverride: _root);

    [Fact]
    public async Task LettingGo_DeletesTheRecording_AndItsHistoryEntry()
    {
        var history = NewHistory();
        string recording = NewRecording();
        await history.AddImageAsync(recording);
        var entry = Assert.Single(await history.GetItemsAsync());

        var unsaved = new UnsavedRecording(recording, history);
        unsaved.Dispose();
        await unsaved.Deletion;

        Assert.False(File.Exists(recording));
        Assert.Empty(await history.GetItemsAsync());
        Assert.False(File.Exists(entry.ThumbnailPath!));
    }

    [Fact]
    public async Task ARecordingOnTheClipboard_StaysOnDisk_AndInTheHistory()
    {
        var history = NewHistory();
        string recording = NewRecording();
        await history.AddImageAsync(recording);

        var unsaved = new UnsavedRecording(recording, history);
        unsaved.Keep();
        unsaved.Dispose();
        await unsaved.Deletion;

        Assert.True(File.Exists(recording));
        Assert.Equal(recording, Assert.Single(await history.GetItemsAsync()).FilePath);
    }

    [Fact]
    public async Task ASavedRecording_LeavesItsHistoryEntryOnTheSavedFile()
    {
        var history = NewHistory();
        string recording = NewRecording();
        await history.AddImageAsync(recording);
        var entry = Assert.Single(await history.GetItemsAsync());
        string saved = Path.Combine(_root, "Videos", "My clip.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        File.Copy(recording, saved);

        var unsaved = new UnsavedRecording(recording, history);
        await unsaved.SavedAsAsync(saved);
        unsaved.Dispose();
        await unsaved.Deletion;

        Assert.False(File.Exists(recording));
        var moved = Assert.Single(await history.GetItemsAsync());
        Assert.Equal(entry.Id, moved.Id);
        Assert.Equal(saved, moved.FilePath);
        Assert.True(File.Exists(moved.ThumbnailPath!));
    }

    [Fact]
    public async Task AFileNotNamedLikeATempRecording_IsNeverDeleted()
    {
        string saved = WriteThumbnailable(Path.Combine(_root, "Videos", "GimmeCapture_20261027_120000.mp4"));

        var unsaved = new UnsavedRecording(saved, history: null);
        unsaved.Dispose();
        await unsaved.Deletion;

        Assert.True(File.Exists(saved));
    }

    [Fact]
    public async Task LettingGoTwice_IsNoError_AndNeedsNoHistory()
    {
        string recording = NewRecording();

        var unsaved = new UnsavedRecording(recording, history: null);
        unsaved.Dispose();
        unsaved.Dispose();
        await unsaved.Deletion;

        Assert.False(File.Exists(recording));
    }
}
