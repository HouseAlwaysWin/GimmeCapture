using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The temporary files a video pin plays from, and who still reads them. Those files are the recording the user has
/// not saved (an <see cref="Media.UnsavedRecording"/> the overlay hands over) and a file of the pin's own in
/// <see cref="PinTempDirectory"/> (a cropped clip, a GIF recording's audio sidecar). The pin reads them, and so does
/// each save, copy or crop it runs, which can outlast the pin: closing it mid-save used to delete the clip the save was
/// still reading. They go once the pin (<see cref="Dispose"/>) and every <see cref="Use"/> are done.
/// <para>A source copied to the clipboard as a file stays, since the clipboard outlives the pin. A start-up sweep
/// removes it later: <see cref="TempWorkspaceCleaner"/> for the pin's own files, <see cref="RecordingTempCleaner"/>
/// for a recording.</para>
/// </summary>
public sealed class PinSourceFiles : IDisposable
{
    private readonly string _sourcePath;

    // The pin itself, plus each save, copy or crop still reading the source.
    private int _uses = 1;
    private int _pinDisposed;
    private volatile bool _onClipboard;

    public PinSourceFiles(string sourcePath, UnsavedRecording? unsavedRecording)
    {
        _sourcePath = sourcePath ?? string.Empty;
        UnsavedRecording = unsavedRecording;
    }

    /// <summary>The recording the user has not saved, when the pin plays one; null for any other clip.</summary>
    public UnsavedRecording? UnsavedRecording { get; }

    /// <summary>A save, copy or crop starts reading the source: the files stay until the returned use is disposed.</summary>
    public IDisposable Use()
    {
        Interlocked.Increment(ref _uses);
        return new SourceUse(this);
    }

    /// <summary>The file at <paramref name="path"/> went to the clipboard; when that is the source, it stays on disk.</summary>
    public void CopiedToClipboard(string path)
    {
        if (!string.Equals(path, _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return; // an export of its own, in a GimmeCapture_Export_* folder
        }

        _onClipboard = true;
        if (UnsavedRecording != null
            && string.Equals(UnsavedRecording.FilePath, _sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            UnsavedRecording.Keep();
        }
    }

    /// <summary>The pin saved its clip as <paramref name="savedPath"/>: the recording's history entry moves there.</summary>
    public Task SavedAsAsync(string savedPath) => UnsavedRecording?.SavedAsAsync(savedPath) ?? Task.CompletedTask;

    /// <summary>The pin closed, and its player has let go of the file.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pinDisposed, 1) == 0)
        {
            EndUse();
        }
    }

    private void EndUse()
    {
        if (Interlocked.Decrement(ref _uses) != 0)
        {
            return;
        }

        if (!_onClipboard)
        {
            PinTempDirectory.TryDeleteOwnedFile(_sourcePath);
        }

        UnsavedRecording?.Dispose();
    }

    private sealed class SourceUse(PinSourceFiles owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndUse();
            }
        }
    }
}
