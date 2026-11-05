using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

/// <summary>
/// The frames a paused video pin shows when it is sought, from one decoder kept between seeks. Each paused seek opened
/// the file again: 130-195 ms a frame on the user's recordings, against 10-55 ms from the kept decoder. Needs only the
/// bundled FFmpeg.
/// </summary>
public sealed class KeptStillFramesTests : IDisposable
{
    private const int Fps = 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public KeptStillFramesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A test that failed may have left a decoder holding a clip.
        }
    }

    [Fact]
    public async Task PausedSeeks_ShareOneOpenOfTheFile_EachLandingOnItsFrame()
    {
        string clip = VideoTestClips.WriteY4m(Path.Combine(_dir, "clip.y4m"), 64, 48, 30, Fps, 1);
        using var frames = new KeptStillFrames();

        foreach (double seconds in new[] { 0.5, 0.6, 0.2, 0.9 })
        {
            byte[]? frame = await frames.DecodeFrameAtAsync(clip, 64, 48, seconds, CancellationToken.None);

            Assert.NotNull(frame);
            Assert.True(frame.Length >= 64 * 48 * 4);
            // The first frame at or after the time asked for.
            Assert.InRange(frames.LastFrameSeconds, seconds - 0.001, seconds + (1.0 / Fps));
        }

        Assert.Equal(1, frames.Opens);
    }

    [Fact]
    public async Task AnotherSizeOrFile_GetsADecoderOfItsOwn()
    {
        string clip = VideoTestClips.WriteY4m(Path.Combine(_dir, "clip.y4m"), 64, 48, 30, Fps, 1);
        string shorter = VideoTestClips.WriteY4m(Path.Combine(_dir, "shorter.y4m"), 64, 48, 6, Fps, 1); // 0.2 s
        using var frames = new KeptStillFrames();

        Assert.NotNull(await frames.DecodeFrameAtAsync(clip, 64, 48, 0.5, CancellationToken.None));
        byte[]? smaller = await frames.DecodeFrameAtAsync(clip, 32, 24, 0.5, CancellationToken.None);
        Assert.NotNull(smaller);
        Assert.Equal(32 * 24 * 4, smaller.Length);

        // The shorter clip has no frame half a second in; the first clip's decoder would have given one.
        Assert.Null(await frames.DecodeFrameAtAsync(shorter, 32, 24, 0.5, CancellationToken.None));
    }

    [Fact]
    public async Task Released_ItHoldsNoFile_UntilTheNextSeekOpensItAgain()
    {
        string clip = VideoTestClips.WriteY4m(Path.Combine(_dir, "clip.y4m"), 64, 48, 30, Fps, 1);
        using var frames = new KeptStillFrames();
        await frames.DecodeFrameAtAsync(clip, 64, 48, 0.2, CancellationToken.None);

        frames.Release();

        Assert.Equal(0, frames.Opens);
        File.Delete(clip); // nothing holds it open any more
        string again = VideoTestClips.WriteY4m(clip, 64, 48, 30, Fps, 1);
        Assert.NotNull(await frames.DecodeFrameAtAsync(again, 64, 48, 0.4, CancellationToken.None));
        Assert.Equal(1, frames.Opens);
    }
}
