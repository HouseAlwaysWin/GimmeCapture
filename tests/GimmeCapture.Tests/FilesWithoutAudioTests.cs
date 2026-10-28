using System;
using System.IO;
using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

// A pinned recording with no audio stream failed to start its preview audio at every play and seek, and each failure
// was logged as a warning. The player now asks a probe, once per file and only after a failure, whether the file has
// an audio stream at all. These decide which failures that turns into silence and which stay failures.
public class FilesWithoutAudioTests
{
    private const string Silent = "silent-recording.mp4";
    private const string Talking = "recording-with-sound.mp4";

    private int _probes;

    private FilesWithoutAudio FilesProbedBy(Func<string, bool> hasAudioStream) => new(path =>
    {
        _probes++;
        return hasAudioStream(path);
    });

    [Fact]
    public void AFileWithNoAudioStream_IsAdded_AfterOneProbe()
    {
        FilesWithoutAudio files = FilesProbedBy(path => path != Silent);

        Assert.True(files.AddIfSilent(Silent));
        Assert.True(files.Contains(Silent));
        Assert.True(files.AddIfSilent(Silent));
        Assert.Equal(1, _probes);
    }

    [Fact]
    public void AFileWithAudio_StaysAFailure_AndIsProbedOnce()
    {
        // No device, say: every start fails, and each is still reported, but the file is opened for the probe only once.
        FilesWithoutAudio files = FilesProbedBy(_ => true);

        Assert.False(files.AddIfSilent(Talking));
        Assert.False(files.AddIfSilent(Talking));
        Assert.False(files.Contains(Talking));
        Assert.Equal(1, _probes);
    }

    [Fact]
    public void AFileTheProbeCannotRead_CountsAsHavingAudio_AndIsProbedAgainNextTime()
    {
        FilesWithoutAudio files = FilesProbedBy(_ => throw new IOException("the file is locked"));

        Assert.False(files.AddIfSilent(Silent));
        Assert.False(files.Contains(Silent));
        Assert.False(files.AddIfSilent(Silent));
        Assert.Equal(2, _probes);
    }

    [Fact]
    public void AskingBeforeAStart_NeverProbes()
    {
        // Every start asks first, so a file whose audio plays must not cost a probe.
        FilesWithoutAudio files = FilesProbedBy(_ => false);

        Assert.False(files.Contains(Silent));
        Assert.Equal(0, _probes);
    }

    [Fact]
    public void EachFile_IsDecidedOnItsOwn()
    {
        FilesWithoutAudio files = FilesProbedBy(path => path == Talking);

        Assert.True(files.AddIfSilent(Silent));
        Assert.False(files.AddIfSilent(Talking));

        Assert.True(files.Contains(Silent));
        Assert.False(files.Contains(Talking));
    }
}
