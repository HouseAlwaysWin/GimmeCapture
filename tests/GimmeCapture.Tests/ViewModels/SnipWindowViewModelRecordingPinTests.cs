using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The pin a finished recording opens in: it decodes the recording at its own pixels and shows them pixel for pixel.
/// It was given the region's size in DIPs as both, so at 125% a recording of a region 907.2 DIPs wide, 1134 pixels,
/// was decoded at 907 pixels and stretched back up to the screen.
/// </summary>
public sealed class SnipWindowViewModelRecordingPinTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "RecordingPin", Guid.NewGuid().ToString("N"));
    private readonly string _recording;

    public SnipWindowViewModelRecordingPinTests()
    {
        Directory.CreateDirectory(_folder);
        _recording = Path.Combine(_folder, "recording.mp4");
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
    [InlineData(1.25, 907.2, 499.2, 1134, 624)]
    [InlineData(1.0, 640.0, 360.0, 640, 360)]
    [InlineData(1.5, 400.0, 300.0, 600, 450)]
    public async Task ARecordingsPin_DecodesItsPixels_AndShowsThemOneForOne(
        double scaling, double regionWidth, double regionHeight, int pixelWidth, int pixelHeight)
    {
        using var vm = OverlayWithFinishedRecording(new Rect(40, 30, regionWidth, regionHeight), scaling);
        (int Width, int Height, double DipWidth, double DipHeight)? pinned = null;
        vm.OpenPinnedVideoWindowAction = (path, pw, ph, ow, oh, _, _, _, _, _) => pinned = (pw, ph, ow, oh);

        await (Task)typeof(SnipWindowViewModel)
            .GetMethod("ExecutePinRecordingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, null)!;

        Assert.NotNull(pinned);
        Assert.Equal(pixelWidth, pinned.Value.Width);
        Assert.Equal(pixelHeight, pinned.Value.Height);
        Assert.Equal(pixelWidth, pinned.Value.DipWidth * scaling, 6);
        Assert.Equal(pixelHeight, pinned.Value.DipHeight * scaling, 6);
    }

    // An overlay at `scaling` whose recording of `region` has just finished at _recording.
    private SnipWindowViewModel OverlayWithFinishedRecording(Rect region, double scaling)
    {
        var settings = new Mock<ISnipOverlaySettings>();
        settings.SetupGet(s => s.RecordingSettings).Returns(new RecordingSettingsViewModel { VideoSaveDirectory = _folder });

        var vm = new SnipWindowViewModel(
            Colors.Red, 2.0, new Mock<IScreenCaptureService>().Object,
            recService: FinishedRecording(_recording), settings: settings.Object)
        {
            VisualScaling = scaling,
        };
        SetField(vm, "_currentRecordingPath", _recording);
        SetField(vm, "_recordingCaptureLogicalRect", (Rect?)region);
        vm.CloseAction = () => { };
        return vm;
    }

    // RecordingService names its output only while it records for real, so the test hands it the finished file.
    private static RecordingService FinishedRecording(string path)
    {
        var service = new RecordingService(new FFmpegDownloaderService());
        SetField(service, "_outputFile", path);
        return service;
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
