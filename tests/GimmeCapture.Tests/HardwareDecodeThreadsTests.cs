using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A decoder with a D3D11VA device decodes on one thread (<see cref="LibavVideoFramePlayer.DecodeThreads"/>): FFmpeg
/// sizes a hardware decoder's surface pool for its frame threads too. The probe measures what that holds and costs on a
/// real video, for the Compress editor's frames and for a pin's playback.
/// </summary>
public sealed class HardwareDecodeThreadsTests(ITestOutputHelper output)
{
    private const string FileVariable = "HW_DECODE_THREADS_PROBE_FILE";

    [Fact]
    public void AHardwareDecoder_GetsOneThread_ASoftwareOneLibavcodecsOwnCount()
    {
        Assert.Equal(1, LibavVideoFramePlayer.DecodeThreads(hardware: true));
        Assert.Equal(0, LibavVideoFramePlayer.DecodeThreads(hardware: false));
    }

    /// <summary>
    /// The editor's frames (a 720-wide <see cref="LibavStillFrameDecoder"/>: an open a quarter in, a seek to three
    /// quarters, ten steps forward and one back) and a pin's playback (up to ten seconds at 1280x720 from a quarter in:
    /// frames shown), on the GPU with one thread, on the GPU with frame threads as before, and on the CPU, with the
    /// process's private bytes. Opt-in: HW_DECODE_THREADS_PROBE_FILE names the video, a 4K phone video for the case
    /// that matters.
    /// </summary>
    [OptInFact(FileVariable)]
    public async Task OneThreadUnderD3D11VA_Measured()
    {
        string input = Environment.GetEnvironmentVariable(FileVariable)!;
        using (var probe = new LibavVideoFramePlayer())
        {
            var info = await probe.ProbeAsync(input);
            _duration = info.Duration;
            output.WriteLine($"{System.IO.Path.GetFileName(input)}: {info.Width}x{info.Height}, {info.Duration:F1} s");
        }

        bool hardwareBefore = LibavVideoFramePlayer.HardwareDecodeEnabled;
        try
        {
            foreach (string mode in new[] { "GPU, one thread", "GPU, frame threads", "CPU", "CPU", "GPU, frame threads", "GPU, one thread" })
            {
                LibavVideoFramePlayer.HardwareDecodeEnabled = mode != "CPU";
                LibavVideoFramePlayer.FrameThreadsUnderHardwareForTest = mode == "GPU, frame threads";
                output.WriteLine($"{mode}: editor {await EditorFrames(input)}; pin {await PinPlayback(input)}");
            }
        }
        finally
        {
            LibavVideoFramePlayer.HardwareDecodeEnabled = hardwareBefore;
            LibavVideoFramePlayer.FrameThreadsUnderHardwareForTest = false;
        }
    }

    private double _duration;

    private async Task<string> EditorFrames(string input)
    {
        long start = PrivateMegabytes();
        var decoder = new LibavStillFrameDecoder(input, 720, 404);
        var watch = Stopwatch.StartNew();
        await decoder.DecodeFrameAtAsync(_duration / 4);
        double open = watch.Elapsed.TotalMilliseconds;
        double farSeconds = _duration * 3 / 4;
        watch.Restart();
        await decoder.DecodeFrameAtAsync(farSeconds);
        double far = watch.Elapsed.TotalMilliseconds;
        var steps = new double[10];
        for (int i = 0; i < steps.Length; i++)
        {
            watch.Restart();
            await decoder.DecodeFrameAtAsync(farSeconds + ((i + 1) / 29.97));
            steps[i] = watch.Elapsed.TotalMilliseconds;
        }

        watch.Restart();
        await decoder.DecodeFrameAtAsync(farSeconds + (5 / 29.97));
        double back = watch.Elapsed.TotalMilliseconds;
        long whileOpen = PrivateMegabytes();
        decoder.Dispose();
        long closed = PrivateMegabytes();
        return $"open {open:F0} ms, far seek {far:F0}, steps {steps.Average():F0} (max {steps.Max():F0}), back {back:F0}; " +
            $"private {start} -> {whileOpen} MB open -> {closed} closed";
    }

    // Ten seconds of a pin's playback: how many frames reached the screen, and how far into the video it got.
    private async Task<string> PinPlayback(string input)
    {
        long start = PrivateMegabytes();
        long peak = start;
        int frames = 0;
        double first = double.NaN;
        double last = double.NaN;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var player = new LibavVideoFramePlayer();
        try
        {
            await player.PlayFramesAsync(input, 1280, 720, _duration / 4, 1.0, false, (_, seconds) =>
            {
                frames++;
                first = double.IsNaN(first) ? seconds : first;
                last = seconds;
                if (frames % 30 == 0)
                {
                    peak = Math.Max(peak, PrivateMegabytes());
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return $"{frames} frames over {last - first:F1} s of video ({frames / Math.Max(0.001, last - first):F1} a second), " +
            $"private {start} -> {peak} MB at most";
    }

    private static long PrivateMegabytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64 / 1048576;
    }
}
