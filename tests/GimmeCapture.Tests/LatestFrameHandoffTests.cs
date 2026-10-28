using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

// A playing video pin's frames cross from the decode thread to the UI thread here. It replaced one shared buffer that
// the UI copied out of with no lock held, so a frame could be drawn half old and half new, and that was always copied
// whole, up to twice the frame.
public class LatestFrameHandoffTests
{
    private static byte[] Frame(byte value, int length = 16)
    {
        var frame = new byte[length];
        Array.Fill(frame, value);
        return frame;
    }

    [Fact]
    public void Copying_GivesTheNewestFrame_Once()
    {
        using var handoff = new LatestFrameHandoff();
        handoff.Publish(Frame(1), generation: 7);
        handoff.Publish(Frame(2), generation: 7);
        var bitmap = new byte[16];

        Assert.True(handoff.HasNewFrame(7));
        Assert.Equal(16, handoff.CopyLatestTo(bitmap, generation: 7));
        Assert.Equal(Frame(2), bitmap);
        Assert.False(handoff.HasNewFrame(7));
        Assert.Equal(0, handoff.CopyLatestTo(bitmap, generation: 7));
    }

    [Fact]
    public void OnlyTheFramesLength_IsCopied_NotThePooledBufferItSitsIn()
    {
        using var handoff = new LatestFrameHandoff();
        handoff.Publish(Frame(5, length: 1000), generation: 1); // the pool hands out 1024 bytes for this
        var bitmap = Frame(9, length: 2000);

        Assert.Equal(1000, handoff.CopyLatestTo(bitmap, generation: 1));
        Assert.Equal(Frame(5, length: 1000), bitmap[..1000]);
        Assert.Equal(Frame(9, length: 1000), bitmap[1000..]);
    }

    [Fact]
    public void AFrameFromAPlaybackASeekReplaced_IsNotShown()
    {
        using var handoff = new LatestFrameHandoff();
        handoff.Publish(Frame(1), generation: 3);
        var bitmap = new byte[16];

        Assert.False(handoff.HasNewFrame(4));
        Assert.Equal(0, handoff.CopyLatestTo(bitmap, generation: 4));
        Assert.Equal(new byte[16], bitmap);
    }

    [Fact]
    public void AfterDispose_NothingIsHandedOver()
    {
        var handoff = new LatestFrameHandoff();
        handoff.Publish(Frame(1), generation: 1);
        handoff.Dispose();
        handoff.Publish(Frame(2), generation: 1);

        Assert.False(handoff.HasNewFrame(1));
        Assert.Equal(0, handoff.CopyLatestTo(new byte[16], generation: 1));
    }

    [Fact]
    public async Task AFrameCopiedWhileTheDecoderRuns_IsNeverHalfOneFrameAndHalfAnother()
    {
        using var handoff = new LatestFrameHandoff();
        using var stop = new CancellationTokenSource();

        // Each frame is a single byte value throughout, so a frame that mixes two shows up at once.
        Task decoder = Task.Run(() =>
        {
            var source = new byte[64 * 1024];
            byte value = 0;
            while (!stop.IsCancellationRequested)
            {
                Array.Fill(source, ++value);
                handoff.Publish(source, generation: 1);
            }
        });

        try
        {
            int copied = CopyFramesCheckingEach(handoff, frames: 300, TimeSpan.FromSeconds(5));
            Assert.True(copied > 0, "the UI never got a frame");
        }
        finally
        {
            stop.Cancel();
            await decoder;
        }
    }

    // The UI side: copies frames as they come and checks each is whole. Returns how many it copied.
    private static int CopyFramesCheckingEach(LatestFrameHandoff handoff, int frames, TimeSpan timeout)
    {
        var bitmap = new byte[64 * 1024];
        int copied = 0;
        var elapsed = Stopwatch.StartNew();
        while (copied < frames && elapsed.Elapsed < timeout)
        {
            if (handoff.CopyLatestTo(bitmap, generation: 1) == 0)
            {
                continue;
            }

            int mixedAt = bitmap.AsSpan().IndexOfAnyExcept(bitmap[0]);
            Assert.True(mixedAt < 0, $"frame {copied} changes from {bitmap[0]} to {(mixedAt >= 0 ? bitmap[mixedAt] : 0)} at byte {mixedAt}");
            copied++;
        }

        return copied;
    }
}
