using System;
using System.IO;
using System.Reactive.Threading.Tasks;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// Stopping a recording moves the finished file out of the temp folder to where it is kept: the path picked in the save
/// dialog, or the video folder when there is no dialog to ask. To another drive that move is a full copy, and it ran on
/// the UI thread the stop command runs on, so the app and its hotkeys froze until it was done.
/// </summary>
public sealed class SnipWindowViewModelRecordingSaveTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "RecordingSave", Guid.NewGuid().ToString("N"));
    private readonly string _recording;
    private readonly string _videoFolder;
    private bool _closed;

    public SnipWindowViewModelRecordingSaveTests()
    {
        string tempFolder = Path.Combine(_folder, "temp");
        _videoFolder = Path.Combine(_folder, "videos");
        Directory.CreateDirectory(tempFolder);
        Directory.CreateDirectory(_videoFolder);
        _recording = Path.Combine(tempFolder, "recording.mp4");
        File.WriteAllText(_recording, "the recording");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // %TEMP% clean-up gets it later.
        }
    }

    [Theory]
    [InlineData(true)]   // saved to the path picked in the save dialog
    [InlineData(false)]  // no dialog to ask, as after an automatic stop: saved to the video folder
    public async Task TheMove_RunsOffTheThreadTheStopRunsOn_AndTheOverlayClosesAfterIt(bool saveDialog)
    {
        using var vm = Overlay(saveDialog ? Path.Combine(_videoFolder, "picked.mp4") : null);
        using var moveStarted = new ManualResetEventSlim();
        using var releaseMove = new ManualResetEventSlim();
        int stopThread = Environment.CurrentManagedThreadId;
        int moveThread = 0;
        vm.UseRecordingFileMoveForTest((source, target) =>
        {
            moveThread = Environment.CurrentManagedThreadId;
            moveStarted.Set();
            // On the stop's own thread nothing could let it go, so it is not held there. That would be the freeze this
            // guards against, and the thread check below fails on it.
            if (moveThread != stopThread)
            {
                releaseMove.Wait(TimeSpan.FromSeconds(30));
            }

            File.Move(source, target);
        });

        Task stop = vm.StopRecordingCommand.Execute().ToTask();
        try
        {
            Assert.True(moveStarted.Wait(TimeSpan.FromSeconds(30)), "the recording was never moved");
            Assert.NotEqual(stopThread, moveThread);
            Assert.False(stop.IsCompleted, "the save finished before the recording was in place");
            Assert.False(_closed, "the overlay closed before the recording was in place");
        }
        finally
        {
            releaseMove.Set();
        }

        await stop;

        Assert.True(_closed);
        Assert.False(File.Exists(_recording));
        string saved = Assert.Single(Directory.GetFiles(_videoFolder));
        Assert.Equal("the recording", File.ReadAllText(saved));
    }

    [Fact]
    public async Task TheMove_ReplacesAFileAlreadyAtThePickedPath()
    {
        string picked = Path.Combine(_videoFolder, "picked.mp4");
        File.WriteAllText(picked, "an older file");
        using var vm = Overlay(picked);

        await vm.StopRecordingCommand.Execute().ToTask();

        Assert.Equal("the recording", File.ReadAllText(picked));
        Assert.False(File.Exists(_recording));
        Assert.True(_closed);
    }

    [Fact]
    public async Task AFailedMove_LeavesTheRecordingWhereItIs()
    {
        // A folder that does not exist: the move fails, on the thread pool now, and the save still has to catch it.
        using var vm = Overlay(Path.Combine(_folder, "missing", "picked.mp4"));

        await vm.StopRecordingCommand.Execute().ToTask();

        Assert.Equal("the recording", File.ReadAllText(_recording));
        Assert.True(_closed);
    }

    // An overlay whose recording has just finished at _recording. With a picked path its save dialog returns that path;
    // without one there is no dialog, as when the overlay closed or the recording stopped by itself.
    private SnipWindowViewModel Overlay(string? pickedPath)
    {
        var recordingSettings = new RecordingSettingsViewModel { VideoSaveDirectory = _videoFolder };
        var settings = new Mock<ISnipOverlaySettings>();
        settings.SetupGet(s => s.RecordingSettings).Returns(recordingSettings);
        // RevealAfterSave stays false, so no Explorer window opens.

        var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, new Mock<IScreenCaptureService>().Object,
            recService: FinishedRecording(_recording), settings: settings.Object);
        if (pickedPath != null)
        {
            vm.PickSaveFileAction = () => Task.FromResult<string?>(pickedPath);
        }

        vm.CloseAction = () => _closed = true;
        return vm;
    }

    // RecordingService names its output only while it records for real, so the test hands it the finished file.
    private static RecordingService FinishedRecording(string path)
    {
        var service = new RecordingService(new FFmpegDownloaderService());
        typeof(RecordingService)
            .GetField("_outputFile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, path);
        return service;
    }
}
