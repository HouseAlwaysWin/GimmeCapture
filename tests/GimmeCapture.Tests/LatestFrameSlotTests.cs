using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Tests;

// The hand-over of decoded frames from a decode thread to the UI thread in the compress editor and compare view:
// only the newest frame waits, one UI pass is scheduled per batch of frames, and the buffers are reused.
public class LatestFrameSlotTests
{
    private const int Width = 4;
    private const int Height = 2;

    private static byte[] FrameFilledWith(byte value, int extra = 0)
    {
        var bytes = new byte[(Width * Height * 4) + extra];
        Array.Fill(bytes, value);
        return bytes;
    }

    [Fact]
    public void Offer_AsksForOnePassUntilTheFrameIsTaken()
    {
        var slot = new LatestFrameSlot();

        Assert.True(slot.Offer(FrameFilledWith(1), Width, Height, ticket: 7, seconds: 1.0));
        Assert.False(slot.Offer(FrameFilledWith(2), Width, Height, ticket: 7, seconds: 1.1));
        Assert.False(slot.Offer(FrameFilledWith(3), Width, Height, ticket: 7, seconds: 1.2));

        Assert.NotNull(slot.Take());
        Assert.True(slot.Offer(FrameFilledWith(4), Width, Height, ticket: 7, seconds: 1.3));
    }

    [Fact]
    public void Take_ReturnsOnlyTheNewestFrame()
    {
        var slot = new LatestFrameSlot();
        slot.Offer(FrameFilledWith(1), Width, Height, ticket: 1, seconds: 0.5);
        slot.Offer(FrameFilledWith(2), Width, Height, ticket: 2, seconds: 0.6);

        LatestFrameSlot.Frame frame = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());

        Assert.Equal(2, frame.Ticket);
        Assert.Equal(0.6, frame.Seconds);
        Assert.Equal(Width, frame.Width);
        Assert.Equal(Height, frame.Height);
        Assert.All(frame.Pixels.AsSpan(0, Width * Height * 4).ToArray(), b => Assert.Equal((byte)2, b));
        Assert.Null(slot.Take());
    }

    [Fact]
    public void Offer_CopiesThePixels_SoTheDecoderCanReuseItsBuffer()
    {
        var slot = new LatestFrameSlot();
        byte[] decoderBuffer = FrameFilledWith(5);
        slot.Offer(decoderBuffer, Width, Height, ticket: 0, seconds: 0);

        Array.Fill(decoderBuffer, (byte)9); // the decoder writes its next frame into the same buffer

        LatestFrameSlot.Frame frame = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());
        Assert.All(frame.Pixels.AsSpan(0, Width * Height * 4).ToArray(), b => Assert.Equal((byte)5, b));
    }

    [Fact]
    public void Offer_TakesOnlyTheFrameFromALongerBuffer()
    {
        // The decoder hands over a pooled buffer, which can be longer than the frame.
        var slot = new LatestFrameSlot();
        Assert.True(slot.Offer(FrameFilledWith(3, extra: 100), Width, Height, ticket: 0, seconds: 0));

        LatestFrameSlot.Frame frame = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());
        Assert.True(frame.Pixels.Length >= Width * Height * 4);
    }

    [Fact]
    public void Offer_IgnoresAShortBuffer()
    {
        var slot = new LatestFrameSlot();

        Assert.False(slot.Offer(new byte[(Width * Height * 4) - 1], Width, Height, ticket: 0, seconds: 0));
        Assert.Null(slot.Take());
    }

    [Fact]
    public void ReturnedBuffers_AreReusedForLaterFrames()
    {
        var slot = new LatestFrameSlot();
        slot.Offer(FrameFilledWith(1), Width, Height, ticket: 0, seconds: 0);
        LatestFrameSlot.Frame first = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());
        slot.Return(first.Pixels);

        slot.Offer(FrameFilledWith(2), Width, Height, ticket: 0, seconds: 0);
        LatestFrameSlot.Frame second = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());

        Assert.Same(first.Pixels, second.Pixels);
    }

    [Fact]
    public void AReplacedFramesBuffer_TakesALaterFrame()
    {
        var slot = new LatestFrameSlot();
        slot.Offer(FrameFilledWith(1), Width, Height, ticket: 0, seconds: 0);
        slot.Offer(FrameFilledWith(2), Width, Height, ticket: 0, seconds: 0); // replaces the first, never drawn
        LatestFrameSlot.Frame shown = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());

        slot.Offer(FrameFilledWith(3), Width, Height, ticket: 0, seconds: 0);
        LatestFrameSlot.Frame next = Assert.IsType<LatestFrameSlot.Frame>(slot.Take());

        Assert.NotSame(shown.Pixels, next.Pixels); // the first frame's buffer, not the one still being drawn
        Assert.All(next.Pixels.AsSpan(0, Width * Height * 4).ToArray(), b => Assert.Equal((byte)3, b));
    }

    [Fact]
    public async Task ConcurrentDecoderAndUiThread_NeverSeeATornOrOlderFrame()
    {
        // Each frame is filled with its own number: a frame mixing two numbers was overwritten while being drawn.
        const int frameCount = 3000;
        var slot = new LatestFrameSlot();
        using var stop = new CancellationTokenSource();

        Task decoder = Task.Run(() =>
        {
            var buffer = new byte[Width * Height * 4];
            for (int i = 1; i <= frameCount; i++)
            {
                Array.Fill(buffer, (byte)(i % 251));
                slot.Offer(buffer, Width, Height, ticket: i, seconds: i);
            }
        });

        int lastTicket = 0;
        Task ui = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (slot.Take() is not { } frame)
                {
                    Thread.Yield();
                    continue;
                }

                byte expected = (byte)(frame.Ticket % 251);
                for (int b = 0; b < Width * Height * 4; b++)
                {
                    Assert.Equal(expected, frame.Pixels[b]);
                }

                Assert.True(frame.Ticket > lastTicket, $"frame {frame.Ticket} came after {lastTicket}");
                lastTicket = frame.Ticket;
                slot.Return(frame.Pixels);
            }
        });

        await decoder;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref lastTicket) != frameCount && !ui.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }

        stop.Cancel();
        await ui;
        Assert.Equal(frameCount, lastTicket);
    }
}
