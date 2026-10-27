using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// A finished recording that exists only in the app's temp folder, because the user pinned it instead of saving it.
/// Nothing used to delete one: saving from the pin copies it elsewhere, and closing the pin left it behind, about
/// 100 MB of them on one machine. The pin playing it owns this and disposes it once the pin, and every save or export
/// still reading the file, is done: the file goes then, and so does the history entry pointing at it.
/// <para>A file that went somewhere that outlives the pin, the clipboard, is kept (<see cref="Keep"/>);
/// <see cref="RecordingTempCleaner"/> removes it at a later start, once it is a day old.</para>
/// </summary>
public sealed class UnsavedRecording : IDisposable
{
    private readonly CaptureHistoryService? _history;
    private int _disposed;
    private volatile bool _kept;

    public UnsavedRecording(string filePath, CaptureHistoryService? history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _history = history;
    }

    public string FilePath { get; }

    /// <summary>The deletion <see cref="Dispose"/> started; done at once when there was nothing to delete.</summary>
    internal Task Deletion { get; private set; } = Task.CompletedTask;

    /// <summary>The file itself went somewhere that outlives the pin, the clipboard: leave it on disk.</summary>
    public void Keep() => _kept = true;

    /// <summary>
    /// The user saved the recording as <paramref name="savedPath"/>. Its history entry moves there, so deleting this
    /// copy does not take the recording out of the history. Never throws.
    /// </summary>
    public Task SavedAsAsync(string savedPath) =>
        _history == null || string.IsNullOrWhiteSpace(savedPath)
            ? Task.CompletedTask
            : _history.RetargetAsync(FilePath, savedPath);

    /// <summary>Lets go of the recording, deleting it unless it was kept. The deletion runs on the thread pool.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1 || _kept)
        {
            return;
        }

        // Only a file named the way the overlay names a recording in the temp folder: never one the user saved.
        if (!RecordingTempDirectory.IsRecordingFileName(Path.GetFileName(FilePath)))
        {
            AppLog.Warning("UnsavedRecording.Delete", "Not a temp recording; left alone.");
            return;
        }

        Deletion = Task.Run(DeleteAsync);
    }

    private async Task DeleteAsync()
    {
        // The history entry first: that waits for a thumbnail the history may still be reading from the file.
        if (_history != null)
        {
            await _history.ForgetAsync(FilePath).ConfigureAwait(false);
        }

        try
        {
            File.Delete(FilePath);
            AppLog.Debug("UnsavedRecording.Delete", $"Deleted the unsaved recording {Path.GetFileName(FilePath)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open in a player, say: RecordingTempCleaner removes it at a later start.
            AppLog.Debug("UnsavedRecording.Delete", ex);
        }
    }
}
