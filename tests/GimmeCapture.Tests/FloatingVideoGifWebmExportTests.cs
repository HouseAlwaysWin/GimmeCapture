using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Floating;

namespace GimmeCapture.Tests;

/// <summary>
/// A pinned video's GIF/WebM export (<see cref="FloatingVideoViewModel.TranscodeGifWebmAsync"/>). Its WebM decoded the
/// whole audio track into memory and wrote the WAV on the UI thread, and the MP4 a cut was trimmed to stayed in the
/// temp folder, beside a folder of its own that a failed export left too. Real libav on tiny synthetic clips
/// (<see cref="VideoTestClips"/>), so the tests need only the bundled FFmpeg and run everywhere.
/// </summary>
public sealed class FloatingVideoGifWebmExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public FloatingVideoGifWebmExportTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ACutsWebm_KeepsTheAudio_AndLeavesOnlyItsOwnFile()
    {
        string cut = VideoTestClips.WriteWithTone(Path.Combine(ExportFolder(), "output.mkv"), 64, 48, 60, 30);

        string? webm = await FloatingVideoViewModel.TranscodeGifWebmAsync(
            cut, intermediate: cut, ".webm", VideoQuality.Low, keepAudio: true, _root);

        Assert.NotNull(webm);
        Assert.Equal(60, VideoTestClips.Read(webm).Frames);
        Assert.NotEmpty(LibavPinAudioPcmDecoder.Decode(webm, 0).PcmBytes);
        Assert.False(Directory.Exists(Path.GetDirectoryName(cut)), "the cut's MP4 was left behind");
        string folder = Path.GetDirectoryName(webm)!;
        Assert.Equal([folder], Directory.GetDirectories(_root));
        Assert.Equal([webm], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task ACutGif_IsMadeStraightFromTheRecording_FromWhatIsKept()
    {
        string recording = VideoTestClips.WriteConstantRate(Path.Combine(_root, "recording.mkv"), 64, 48, 90, 30);
        var ranges = new[] { new LibavClipExporter.SourceRange(0, 1), new LibavClipExporter.SourceRange(2, 3) };

        string? gif = await FloatingVideoViewModel.TranscodeGifWebmAsync(
            recording, intermediate: null, ".gif", VideoQuality.Low, keepAudio: false, _root, ranges);

        Assert.NotNull(gif);
        VideoTestClips.Timeline timeline = VideoTestClips.Read(gif);
        (int fps, _) = LibavGifTranscoder.QualityLadder(VideoQuality.Low);
        Assert.Equal(2 * fps, timeline.Frames);
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task AGifOfTheWholeRecording_LeavesTheRecordingAlone()
    {
        string recording = VideoTestClips.WriteConstantRate(Path.Combine(_root, "recording.mkv"), 64, 48, 30, 30);

        string? gif = await FloatingVideoViewModel.TranscodeGifWebmAsync(
            recording, intermediate: null, ".gif", VideoQuality.Low, keepAudio: false, _root);

        Assert.NotNull(gif);
        Assert.True(new FileInfo(gif).Length > 0);
        Assert.True(File.Exists(recording));
    }

    // The dithering a GIF recording of each quality gets (RecordingService.FinalizeAsGifAsync). The copy dithered with
    // the finest at every quality, which at Medium made a 34-second recording's GIF nearly twice the size.
    [Theory]
    [InlineData(VideoQuality.High, "dither=sierra2_4a")]
    [InlineData(VideoQuality.Medium, "dither=bayer:bayer_scale=3")]
    [InlineData(VideoQuality.Low, "dither=none")]
    public async Task AGifCopy_IsDitheredAsAGifRecordingOfItsQuality(VideoQuality quality, string recordingDither)
    {
        // Thousands of colours, so the dithering shows in every frame.
        string recording = VideoTestClips.WriteColourY4m(Path.Combine(_root, "recording.y4m"), 320, 240, 60);
        (int fps, int maxWidth) = LibavGifTranscoder.QualityLadder(quality);
        string recorded = Path.Combine(_root, "recorded.gif");
        LibavGifTranscoder.TranscodeToGif(recording, recorded, fps, maxWidth, recordingDither);

        string? gif = await FloatingVideoViewModel.TranscodeGifWebmAsync(
            recording, intermediate: null, ".gif", quality, keepAudio: false, _root);

        Assert.NotNull(gif);
        Assert.Equal(File.ReadAllBytes(recorded), File.ReadAllBytes(gif));
    }

    [Fact]
    public async Task AFailedTranscode_LeavesNoFolderBehind()
    {
        string cut = Path.Combine(ExportFolder(), "output.mp4");
        File.WriteAllBytes(cut, [1, 2, 3, 4]);

        await Assert.ThrowsAnyAsync<Exception>(() => FloatingVideoViewModel.TranscodeGifWebmAsync(
            cut, intermediate: cut, ".webm", VideoQuality.Low, keepAudio: true, _root));

        Assert.Empty(Directory.GetDirectories(_root));
    }

    private string ExportFolder() =>
        Directory.CreateDirectory(Path.Combine(_root, "GimmeCapture_Export_" + Guid.NewGuid().ToString("N"))).FullName;
}
