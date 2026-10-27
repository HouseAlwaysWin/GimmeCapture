using System;
using System.IO;
using System.Linq;

namespace GimmeCapture.Tests;

// What the start-up sweep of the recording temp folders may delete, and what it must leave. On the owner's machine the
// versions\<version>\Temp folders held about 100 MB of recordings nobody saved (15 GIFs in 0.44.0's alone, a 14 MB mp4
// from August in the current one) and the empty session folders of recordings that never started.
public sealed class RecordingTempCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    private string Versions => Path.Combine(_root, "instances", "0123456789abcdef", "versions");
    private string CurrentTemp => Path.Combine(Versions, "0.69.0", "Temp");
    private string OldTemp => Path.Combine(Versions, "0.44.0", "Temp");
    private string AppTemp => Path.Combine(_root, "app", "Temp");
    private string PickedTemp => Path.Combine(_root, "picked");

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

    private static string Write(string path, int length = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static string Recording(string directory, string format = "mp4") =>
        Write(Path.Combine(directory, RecordingTempDirectory.NewRecordingFileName(format)));

    private static string Session(string directory)
    {
        string session = Path.Combine(directory, RecordingTempDirectory.NewSessionDirectoryName());
        Write(Path.Combine(session, "segment_0.mkv"));
        Write(Path.Combine(session, "segment_0.wav"));
        return session;
    }

    private RecordingTempCleaner.SweepResult Sweep(TimeSpan later, string? pickedTemp = null) =>
        RecordingTempCleaner.Sweep(
            new RecordingTempCleaner.Layout(Versions, CurrentTemp, AppTemp, pickedTemp),
            DateTime.UtcNow + later);

    [Fact]
    public void ARecording_IsRemovedOnceItIsADayOld_FromTheTempFolderOfEveryVersion()
    {
        string current = Recording(CurrentTemp);
        string old = Recording(OldTemp, "gif");
        string firstVersions = Recording(AppTemp, "mkv");

        Assert.Empty(Sweep(TimeSpan.FromHours(12)).RecordingsRemoved);
        Assert.True(File.Exists(current) && File.Exists(old) && File.Exists(firstVersions));

        var result = Sweep(TimeSpan.FromDays(2));

        Assert.False(File.Exists(current));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(firstVersions));
        Assert.Equal(
            new[] { current, old, firstVersions }.Select(Path.GetFullPath).Order(),
            result.RecordingsRemoved.Select(Path.GetFullPath).Order());
        Assert.Equal(12, result.BytesFreed);
    }

    [Fact]
    public void TheRawSegmentsOfARecording_AreKeptForAWeek_ForRecoveryAfterAFailedSave()
    {
        string session = Session(CurrentTemp);

        Assert.Equal(0, Sweep(TimeSpan.FromDays(3)).SessionsRemoved);
        Assert.True(Directory.Exists(session));

        var result = Sweep(TimeSpan.FromDays(8));

        Assert.Equal(1, result.SessionsRemoved);
        Assert.Equal(8, result.BytesFreed);
        Assert.False(Directory.Exists(session));
        Assert.Empty(result.RecordingsRemoved);
    }

    [Fact]
    public void ASessionFolderWhoseSegmentsAreStillWritten_IsKept_HoweverOldTheFolderItselfIs()
    {
        string session = Session(CurrentTemp);
        string segment = Path.Combine(session, "segment_0.mkv");
        DateTime monthAgo = DateTime.UtcNow - TimeSpan.FromDays(30);
        File.SetLastWriteTimeUtc(Path.Combine(session, "segment_0.wav"), monthAgo);
        Directory.SetLastWriteTimeUtc(session, monthAgo);

        Assert.Equal(0, Sweep(TimeSpan.Zero).SessionsRemoved);
        Assert.True(File.Exists(segment));

        File.SetLastWriteTimeUtc(segment, monthAgo);

        Assert.Equal(1, Sweep(TimeSpan.Zero).SessionsRemoved);
        Assert.False(Directory.Exists(session));
    }

    [Fact]
    public void NothingButTheNamesTheAppGives_IsTouched()
    {
        string[] files =
        {
            Write(Path.Combine(CurrentTemp, "notes.txt")),
            // A saved capture's default name, and a GUID-named file that is no recording.
            Write(Path.Combine(CurrentTemp, "GimmeCapture_20260629_143052.mp4")),
            Write(Path.Combine(CurrentTemp, $"GimmeCapture_{Guid.NewGuid()}.txt")),
            Write(Path.Combine(CurrentTemp, $"GimmeCapture_{Guid.NewGuid():N}.mp4")),
            Write(Path.Combine(CurrentTemp, "Recordings_1", "segment_0.mkv")),
            Write(Path.Combine(CurrentTemp, "Recordings", "segment_0.mkv")),
        };

        var result = Sweep(TimeSpan.FromDays(30));

        Assert.Empty(result.RecordingsRemoved);
        Assert.Equal(0, result.SessionsRemoved);
        Assert.All(files, file => Assert.True(File.Exists(file), file));
    }

    [Fact]
    public void InATempFolderTheUserPicked_OnlyTheRecordingsGo()
    {
        string recording = Recording(PickedTemp, "gif");
        // The app never puts a session folder there, so one named like it is not the app's.
        string session = Session(PickedTemp);
        string theirs = Write(Path.Combine(PickedTemp, "holiday.mp4"));
        string saved = Write(Path.Combine(PickedTemp, "GimmeCapture_20260629_143052.mp4"));

        var result = Sweep(TimeSpan.FromDays(30), PickedTemp);

        Assert.False(File.Exists(recording));
        Assert.Equal(Path.GetFullPath(recording), Path.GetFullPath(Assert.Single(result.RecordingsRemoved)));
        Assert.True(Directory.Exists(session));
        Assert.True(File.Exists(theirs));
        Assert.True(File.Exists(saved));
        Assert.True(Directory.Exists(PickedTemp));
    }

    [Fact]
    public void AnOlderVersionsTempFolder_GoesOnceItIsEmpty_ButItsConfigAndTheCurrentTempFolderStay()
    {
        Directory.CreateDirectory(CurrentTemp);
        Recording(OldTemp);
        string oldConfig = Write(Path.Combine(Versions, "0.44.0", "config.json"));
        string emptyOldTemp = Path.Combine(Versions, "0.45.0", "Temp");
        Directory.CreateDirectory(emptyOldTemp);
        string foreign = Write(Path.Combine(Versions, "0.46.0", "Temp", "keep.txt"));

        Sweep(TimeSpan.FromDays(2));

        Assert.False(Directory.Exists(OldTemp));
        Assert.False(Directory.Exists(emptyOldTemp));
        Assert.True(File.Exists(oldConfig));
        Assert.True(File.Exists(foreign));
        Assert.True(Directory.Exists(CurrentTemp));
    }

    [Fact]
    public void FoldersThatDoNotExist_AreNoError()
    {
        var result = Sweep(TimeSpan.FromDays(30), Path.Combine(_root, "gone"));

        Assert.Empty(result.RecordingsRemoved);
        Assert.Equal(0, result.SessionsRemoved);
        Assert.False(Directory.Exists(_root));
    }

    [WindowsFact]
    public void ARecordingStillOpen_IsLeftForTheNextStart_AndNotReportedAsRemoved()
    {
        string open = Recording(CurrentTemp);
        string closed = Recording(CurrentTemp);

        RecordingTempCleaner.SweepResult result;
        using (new FileStream(open, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = Sweep(TimeSpan.FromDays(2));
        }

        Assert.True(File.Exists(open));
        Assert.False(File.Exists(closed));
        Assert.Equal(Path.GetFullPath(closed), Path.GetFullPath(Assert.Single(result.RecordingsRemoved)));
    }

    [Theory]
    [InlineData("GimmeCapture_8c248545-17eb-4ee9-a3b2-da97b4be9664.mp4", true)]
    [InlineData("GimmeCapture_0f0328ca-064f-4cf6-8232-9583f6eb9aee.gif", true)]
    [InlineData("GimmeCapture_d2b65b6e-0702-4daa-9ae0-4a3043951c1d.mkv", true)]
    [InlineData("GimmeCapture_d2b65b6e-0702-4daa-9ae0-4a3043951c1d.MOV", true)]
    [InlineData("GimmeCapture_d2b65b6e-0702-4daa-9ae0-4a3043951c1d.webm", true)]
    [InlineData("GimmeCapture_20260629_143052.mp4", false)]
    [InlineData("GimmeCapture_d2b65b6e07024daa9ae04a3043951c1d.mp4", false)]
    [InlineData("GimmeCapture_d2b65b6e-0702-4daa-9ae0-4a3043951c1d.txt", false)]
    [InlineData("GimmeCapture_d2b65b6e-0702-4daa-9ae0-4a3043951c1d", false)]
    [InlineData("crop_d2b65b6e07024daa9ae04a3043951c1d.mp4", false)]
    [InlineData("", false)]
    public void RecordingNames_AreTheOnesTheOverlayGives(string name, bool expected)
    {
        Assert.Equal(expected, RecordingTempDirectory.IsRecordingFileName(name));
    }

    [Theory]
    [InlineData("Recordings_c179f06c-6760-4a4e-8cde-6b74340091b5", true)]
    [InlineData("Recordings", false)]
    [InlineData("Recordings_1", false)]
    [InlineData("recordings_c179f06c-6760-4a4e-8cde-6b74340091b5", false)]
    public void SessionFolderNames_AreTheOnesTheRecordingServiceGives(string name, bool expected)
    {
        Assert.Equal(expected, RecordingTempDirectory.IsSessionDirectoryName(name));
    }

    [Fact]
    public void TheNamesTheAppGives_AreRecognizedAsItsOwn()
    {
        Assert.True(RecordingTempDirectory.IsRecordingFileName(RecordingTempDirectory.NewRecordingFileName("gif")));
        Assert.True(RecordingTempDirectory.IsSessionDirectoryName(RecordingTempDirectory.NewSessionDirectoryName()));
    }
}
