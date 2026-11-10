using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A compress on the NVIDIA GPU: the quality scale, what falls back to the processor and how. The fallback runs everywhere,
/// on clips too small for NVENC and on machines with no GPU; the encode on a real GPU is opt-in (NVENC_EXPORT_PROBE=1).
/// </summary>
public sealed class CompressHardwareEncodingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public CompressHardwareEncodingTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(VideoCodec.H264, 22, 28)]
    [InlineData(VideoCodec.H265, 22, 30)]
    [InlineData(VideoCodec.Av1, 22, 37)]
    public void TheQualitySlider_IsMovedOntoNvencsScale(VideoCodec codec, int slider, int cq)
    {
        Assert.Equal(cq, CompressEncodeMath.EncoderScaleCrf(codec, slider, hardware: true));
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    [InlineData(VideoCodec.Av1)]
    public void EveryPlaceOnTheSlider_ComesBackToTheSameSoftwareValue(VideoCodec codec)
    {
        for (int slider = 14; slider <= 40; slider++)
        {
            int cq = CompressEncodeMath.EncoderScaleCrf(codec, slider, hardware: true);
            Assert.Equal(CompressEncodeMath.EncoderScaleCrf(codec, slider), CompressEncodeMath.SoftwareScaleCrf(codec, cq));
        }
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    [InlineData(VideoCodec.Av1)]
    public void TheEstimate_OnTheGpu_IsLargerAtTheSameSlider(VideoCodec codec)
    {
        long software = CompressEncodeMath.EstimateOutputSizeBytes(
            1920, 1080, 30, 60, 0, 0, codec, CompressEncodeMath.EncoderScaleCrf(codec, 22), audioKbps: 0);
        long hardware = CompressEncodeMath.EstimateOutputSizeBytes(
            1920, 1080, 30, 60, 0, 0, codec, CompressEncodeMath.EncoderScaleCrf(codec, 22, hardware: true), audioKbps: 0, hardware: true);

        Assert.Equal(software * 1.3, hardware, tolerance: 2);
    }

    [Theory]
    [InlineData("ultrafast", "p1")]
    [InlineData("veryfast", "p2")]
    [InlineData("fast", "p4")]
    [InlineData("medium", "p5")]
    [InlineData("slow", "p6")]
    [InlineData(null, "p4")]
    public void TheWindowsSpeedChoices_MapToNvencPresets(string? preset, string expected)
    {
        Assert.Equal(expected, LibavHardwareExportEncoder.PresetFor(preset));
    }

    [Theory]
    [InlineData(VideoCodec.H264, "h264_nvenc", AVPixelFormat.AV_PIX_FMT_YUV420P)]
    [InlineData(VideoCodec.H265, "hevc_nvenc", AVPixelFormat.AV_PIX_FMT_YUV420P)]
    [InlineData(VideoCodec.Av1, "av1_nvenc", AVPixelFormat.AV_PIX_FMT_P010LE)] // 10-bit, as the software AV1 path is
    public void EachCodec_HasItsEncoderAndPixels(VideoCodec codec, string encoder, AVPixelFormat pixels)
    {
        Assert.Equal(encoder, LibavHardwareExportEncoder.EncoderName(codec));
        Assert.Equal(pixels, LibavHardwareExportEncoder.PixelFormatFor(codec));
    }

    [Fact]
    public void TheSettings_TellTheEncoderToUseTheGpu()
    {
        Assert.True(OnTheGpu(VideoCodec.H265).ToExportOptions().UseHardwareEncoder);
        Assert.False(OnTheGpu(VideoCodec.H265).InSoftware().ToExportOptions().UseHardwareEncoder);
    }

    [Fact]
    public void SettingsPutBackOnTheProcessor_AreThoseItWouldHaveHad()
    {
        CompressSettingsSnapshot h264 = OnTheGpu(VideoCodec.H264) with { UseTargetSize = true, TargetSizeMB = 25 };
        CompressSettingsSnapshot h265 = OnTheGpu(VideoCodec.H265) with { UseTargetSize = true, TargetSizeMB = 25 };

        Assert.Equal(22, h264.InSoftware().Crf);
        Assert.False(h264.InSoftware().UseHardwareEncoder);
        Assert.True(h264.InSoftware().UseTwoPass);   // x264 alone has true two-pass
        Assert.False(h265.InSoftware().UseTwoPass);
        Assert.Equal(CompressEncodeMath.EncoderScaleCrf(VideoCodec.Av1, 22), OnTheGpu(VideoCodec.Av1).InSoftware().Crf);
    }

    [Theory]
    [InlineData("MP4", true)]
    [InlineData("MKV", true)]
    [InlineData("MOV", true)]
    [InlineData("GIF", false)]
    [InlineData("WebM", false)]
    [InlineData("MP3", false)]
    [InlineData("WAV", false)]
    [InlineData("M4A", false)]
    [InlineData("Opus", false)]
    public void OnlyTheFormatsItEncodes_CanUseTheGpu(string format, bool expected)
    {
        Assert.Equal(expected, MainWindowViewModel.CanUseHardwareEncoderFor(format));
    }

    // The GIF, WebM and audio paths read the snapshot's quality on the software scale to pick a tier.
    [Theory]
    [InlineData("GIF")]
    [InlineData("WebM")]
    [InlineData("MP3")]
    public void AFormatWithItsOwnEncoder_KeepsTheSoftwareScale(string format)
    {
        var vm = new MainWindowViewModel { SelectedCompressFormat = format, CompressUseHardwareEncoder = true, CompressCrf = 22 };
        var preset = new CompressPreset { Codec = VideoCodec.H265, Crf = 22, Format = format, UseHardwareEncoder = true };

        foreach (CompressSettingsSnapshot snapshot in new[] { vm.BuildSettingsSnapshot(), vm.SnapshotFromPreset(preset) })
        {
            Assert.False(snapshot.UseHardwareEncoder);
            Assert.Equal(22, snapshot.Crf);
        }
    }

    [Fact]
    public void TheWindowsSettingsAndASavedPreset_AskForTheGpuAlike()
    {
        var vm = new MainWindowViewModel
        {
            SelectedCompressFormat = "MP4",
            CompressUseHardwareEncoder = true,
            CompressUseTargetSize = true,
            CompressCrf = 22,
        };
        vm.SelectedCompressCodecOption = Array.Find(vm.CompressCodecOptions, o => o.Value == VideoCodec.H264);
        var preset = new CompressPreset
        {
            Codec = VideoCodec.H264, Crf = 22, Format = "MP4", UseHardwareEncoder = true, UseTargetSize = true, TargetSizeMB = 25,
        };

        foreach (CompressSettingsSnapshot snapshot in new[] { vm.BuildSettingsSnapshot(), vm.SnapshotFromPreset(preset) })
        {
            Assert.True(snapshot.UseHardwareEncoder);
            Assert.Equal(28, snapshot.Crf);
            Assert.False(snapshot.UseTwoPass); // x264's statistics: NVENC goes through the corrective pass
        }
    }

    // 64 wide is under what NVENC takes, and most machines this runs on have no GPU. What the processor makes must be what
    // it makes when asked directly: the quality came to it on NVENC's scale.
    [Fact]
    public async Task AVideoTheGpuCannotEncode_IsEncodedOnTheProcessor_AtTheSameQuality()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        string direct = Path.Combine(_dir, "direct.mp4");
        string fellBack = Path.Combine(_dir, "fell-back.mp4");
        int told = 0;

        CompressSettingsSnapshot asked = OnTheGpu(VideoCodec.H264) with { Preset = "ultrafast" };
        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, direct, asked.InSoftware(), 0, 3.0, new Progress<double>(), CancellationToken.None, pauseGate: null));
        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, fellBack, asked, 0, 3.0, new Progress<double>(), CancellationToken.None, pauseGate: null,
            hardwareEncoderFailed: () => told++));

        Assert.Equal(1, told);
        Assert.Equal(new FileInfo(direct).Length, new FileInfo(fellBack).Length);
        Assert.Equal(90, VideoTestClips.Read(fellBack).Frames);
    }

    [Fact]
    public async Task ATargetSizeTheGpuCannotEncode_IsEncodedOnTheProcessor()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30, detailed: true);
        string output = Path.Combine(_dir, "out.mp4");
        int told = 0;

        CompressSettingsSnapshot asked = OnTheGpu(VideoCodec.H264) with { Preset = "ultrafast", UseTargetSize = true, TargetSizeMB = 1 };
        Assert.True(await CompressPipeline.EncodeOneFileAsync(
            clip, output, asked, 200, 3.0, new Progress<double>(), CancellationToken.None, pauseGate: null,
            hardwareEncoderFailed: () => told++));

        Assert.Equal(1, told);
        Assert.Equal(90, VideoTestClips.Read(output).Frames);
    }

    [Fact]
    public void ASampleTheGpuCannotEncode_IsMadeOnTheProcessor()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 30, 30, detailed: true);
        string output = Path.Combine(_dir, "sample.mp4");

        Assert.True(CompressPipeline.TryExportSample(
            clip, [new LibavClipExporter.SourceRange(0, 1)], output, OnTheGpu(VideoCodec.H264) with { Preset = "ultrafast" }));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public async Task ACancelledCompress_DoesNotStartAgainOnTheProcessor()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 30, 30);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        int told = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompressPipeline.EncodeOneFileAsync(
            clip, Path.Combine(_dir, "out.mp4"), OnTheGpu(VideoCodec.H264), 0, 1.0, new Progress<double>(), cancelled.Token,
            pauseGate: null, hardwareEncoderFailed: () => told++));

        Assert.Equal(0, told);
    }

    /// <summary>
    /// A compress of a little over a minute on a real GPU, three chunks joined, in each codec. Prints the speed and the size.
    /// Opt-in: NVENC_EXPORT_PROBE=1, on a machine whose GPU has all three encoders.
    /// </summary>
    [OptInFact("NVENC_EXPORT_PROBE=1")]
    public async Task ChunksFromTheGpu_JoinIntoOneWholeVideo()
    {
        const int Fps = 30, Seconds = 65, Frames = Fps * Seconds;
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 1280, 720, Frames, Fps, detailed: true);

        foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1 })
        {
            string output = Path.Combine(_dir, $"{codec}.mp4");
            int told = 0;
            var clock = Stopwatch.StartNew();
            Assert.True(await CompressPipeline.EncodeOneFileAsync(
                clip, output, OnTheGpu(codec), 0, Seconds, new Progress<double>(), CancellationToken.None, pauseGate: null,
                hardwareEncoderFailed: () => told++));
            double took = clock.Elapsed.TotalSeconds;

            Assert.Equal(0, told);
            VideoTestClips.Timeline timeline = VideoTestClips.Read(output);
            Assert.Equal(Frames, timeline.Frames);
            Assert.Equal(Seconds, timeline.DurationSeconds, 1);
            Assert.Equal(Frames, DecodeEveryFrame(output));
            _output.WriteLine($"{codec}: {took:F1} s ({Seconds / took:F1}x), {new FileInfo(output).Length / 1048576.0:F1} MB");
        }
    }

    // A chunk that does not fit the one before it fails in the decoder, which reading the container's timeline does not see.
    private static unsafe int DecodeEveryFrame(string path)
    {
        AVFormatContext* format = null;
        Assert.True(ffmpeg.avformat_open_input(&format, path, null, null) >= 0);
        AVCodecContext* decoder = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            Assert.True(ffmpeg.avformat_find_stream_info(format, null) >= 0);
            int video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Assert.True(video >= 0);
            AVCodec* codec = ffmpeg.avcodec_find_decoder(format->streams[video]->codecpar->codec_id);
            decoder = ffmpeg.avcodec_alloc_context3(codec);
            Assert.True(ffmpeg.avcodec_parameters_to_context(decoder, format->streams[video]->codecpar) >= 0);
            Assert.True(ffmpeg.avcodec_open2(decoder, codec, null) >= 0);

            int frames = 0;
            while (ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == video)
                {
                    Assert.True(ffmpeg.avcodec_send_packet(decoder, packet) >= 0, "send_packet");
                    frames += Received(decoder, frame);
                }

                ffmpeg.av_packet_unref(packet);
            }

            Assert.True(ffmpeg.avcodec_send_packet(decoder, null) >= 0, "flush");
            return frames + Received(decoder, frame);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&decoder);
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static unsafe int Received(AVCodecContext* decoder, AVFrame* frame)
    {
        int frames = 0;
        int result;
        while ((result = ffmpeg.avcodec_receive_frame(decoder, frame)) >= 0)
        {
            frames++;
            ffmpeg.av_frame_unref(frame);
        }

        Assert.True(result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF, "receive_frame");
        return frames;
    }

    private static CompressSettingsSnapshot OnTheGpu(VideoCodec codec) => new(
        codec, CompressEncodeMath.EncoderScaleCrf(codec, 22, hardware: true), MaxHeight: 0, MaxFps: 0, "medium",
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 0,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false, UseHardwareEncoder: true);
}
