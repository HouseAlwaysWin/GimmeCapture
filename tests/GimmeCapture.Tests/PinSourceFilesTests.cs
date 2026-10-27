using System;
using System.IO;
using System.Threading.Tasks;

namespace GimmeCapture.Tests;

// A video pin's temporary files go when it closes, but not while a save, copy or crop it started still reads them
// (closing a pin mid-save used to delete the clip the save was reading), and not when the clipboard holds the file.
public sealed class PinSourceFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    // A file of the pin's own: a cropped clip, or a GIF recording's audio sidecar.
    private readonly string _pinFile = Path.Combine(PinTempDirectory.EnsureDirectory(), $"crop_{Guid.NewGuid():N}.mp4");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        File.Delete(_pinFile);
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[4]);
        return path;
    }

    private string NewRecording(string format = "mp4") =>
        Write(Path.Combine(_root, "Temp", RecordingTempDirectory.NewRecordingFileName(format)));

    [Fact]
    public async Task ClosingThePin_DeletesTheRecording_AndTheFileItPlaysFrom()
    {
        // A GIF recording's pin plays an audio-bearing sidecar, not the GIF.
        string gif = NewRecording("gif");
        Write(_pinFile);
        var unsaved = new UnsavedRecording(gif, history: null);
        var files = new PinSourceFiles(_pinFile, unsaved);

        files.Dispose();
        await unsaved.Deletion;

        Assert.False(File.Exists(_pinFile));
        Assert.False(File.Exists(gif));
    }

    [Fact]
    public async Task ASaveStillRunning_KeepsTheFiles_UntilItEnds()
    {
        string recording = NewRecording();
        var unsaved = new UnsavedRecording(recording, history: null);
        var files = new PinSourceFiles(recording, unsaved);

        var save = files.Use();
        files.Dispose(); // the pin closes while the save still reads its file
        await unsaved.Deletion;
        Assert.True(File.Exists(recording));

        save.Dispose();
        save.Dispose();
        await unsaved.Deletion;
        Assert.False(File.Exists(recording));
    }

    [Fact]
    public async Task ARecordingCopiedToTheClipboard_StaysAfterThePinCloses()
    {
        string recording = NewRecording();
        var unsaved = new UnsavedRecording(recording, history: null);
        var files = new PinSourceFiles(recording, unsaved);

        files.CopiedToClipboard(recording);
        files.Dispose();
        await unsaved.Deletion;

        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task ASidecarCopiedToTheClipboard_Stays_ButTheGifRecordingStillGoes()
    {
        string gif = NewRecording("gif");
        Write(_pinFile);
        var unsaved = new UnsavedRecording(gif, history: null);
        var files = new PinSourceFiles(_pinFile, unsaved);

        files.CopiedToClipboard(_pinFile);
        files.Dispose();
        await unsaved.Deletion;

        Assert.True(File.Exists(_pinFile));
        Assert.False(File.Exists(gif));
    }

    [Fact]
    public async Task AnExportCopiedToTheClipboard_DoesNotKeepTheSource()
    {
        string recording = NewRecording();
        var unsaved = new UnsavedRecording(recording, history: null);
        var files = new PinSourceFiles(recording, unsaved);

        files.CopiedToClipboard(Path.Combine(_root, "GimmeCapture_Export_1", "output.mp4"));
        files.Dispose();
        await unsaved.Deletion;

        Assert.False(File.Exists(recording));
    }

    [Fact]
    public void ASavedClip_IsNeverDeleted()
    {
        // Pins of a recording that went straight into the video folder, or of separate-window outputs.
        string saved = Write(Path.Combine(_root, "Videos", "clip.mp4"));
        var files = new PinSourceFiles(saved, unsavedRecording: null);

        files.Dispose();

        Assert.True(File.Exists(saved));
    }
}
