#if WINDOWS // WgcFrameExchange lives with the WGC capture source, which only the Windows head builds
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Tests;

// How a window recording's frames cross from the WGC readback to the encoder. Every frame used to be a new array of the
// frame's size, which the encoder then copied twice more, even when nothing had changed. Now the encoder reads the
// latest frame in place, the readback never writes into the frame being read, and the buffers are used again.
public sealed class WgcFrameExchangeTests
{
    private const int Width = 4;
    private const int Height = 3;

    [Fact]
    public void NothingPublished_NothingToRead()
    {
        using var exchange = new WgcFrameExchange();
        byte[]? copy = null;

        Assert.Equal(0, exchange.Sequence);
        Assert.False(exchange.TryRead(0, out _));
        Assert.False(exchange.TryCopyLatest(ref copy, out _, out _));
        Assert.Null(copy);
    }

    [Fact]
    public void AFrame_IsReadOnce_ThenOnlyANewerOneIs()
    {
        using var exchange = new WgcFrameExchange();
        PublishFilled(exchange, 7);

        Assert.Equal(1, exchange.Sequence);
        Assert.True(exchange.TryRead(0, out var first));
        using (first)
        {
            Assert.Equal((1L, Width, Height), (first.Sequence, first.Width, first.Height));
            Assert.All(Pixels(first), value => Assert.Equal(7, value));
        }

        // Nothing came in since: the encoder encodes the picture it already converted.
        Assert.False(exchange.TryRead(1, out _));

        PublishFilled(exchange, 9);
        Assert.True(exchange.TryRead(1, out var second));
        using (second)
        {
            Assert.Equal(2, second.Sequence);
            Assert.All(Pixels(second), value => Assert.Equal(9, value));
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(13, 3)]
    [InlineData(16, 2)]
    [InlineData(1001, 7)]
    public void Rows_StartAlignedLikeFFmpegsOwnFrames(int width, int height)
    {
        var buffer = new WgcFrameExchange.FrameBuffer(width, height);

        Assert.Equal(0L, (long)buffer.Pixels % 64);
        Assert.Equal(0, buffer.Stride % 64);
        Assert.InRange(buffer.Stride, width * 4, width * 4 + 63);
    }

    [Fact]
    public void TheFrameBeingRead_IsNeverHandedOutForWriting()
    {
        using var exchange = new WgcFrameExchange();
        var read = PublishFilled(exchange, 1);
        Assert.True(exchange.TryRead(0, out var reading));

        var written = new HashSet<WgcFrameExchange.FrameBuffer>();
        for (int i = 0; i < 10; i++)
        {
            written.Add(PublishFilled(exchange, (byte)(i + 2)));
        }

        Assert.DoesNotContain(read, written);
        Assert.InRange(written.Count, 1, 2); // with the one being read: three buffers in all
        Assert.All(Pixels(reading), value => Assert.Equal(1, value));

        // Once released, it takes frames again.
        reading.Dispose();
        bool reused = false;
        for (int i = 0; i < 3 && !reused; i++)
        {
            reused = PublishFilled(exchange, 20) == read;
        }

        Assert.True(reused);
    }

    [Fact]
    public void TheLatestFrame_StaysAsItIs_UntilANewerOneReplacesIt()
    {
        using var exchange = new WgcFrameExchange();
        PublishFilled(exchange, 1);
        Assert.True(exchange.TryRead(0, out var frame));
        frame.Dispose();

        // The next frame is being written: the latest is still what a copy, or the next read, sees.
        var next = exchange.Rent(Width, Height);
        Fill(next, 2);
        byte[]? copy = null;
        Assert.True(exchange.TryCopyLatest(ref copy, out _, out _));
        Assert.All(copy!, value => Assert.Equal(1, value));
        exchange.Return(next);
    }

    [Fact]
    public void ASteadyStreamOfFrames_AllocatesNothing()
    {
        using var exchange = new WgcFrameExchange();
        long sequence = 0;
        int misses = 0;

        // The worst overlap: two frames come in while the encoder still reads the one before them.
        void Cycle()
        {
            if (!exchange.TryRead(sequence, out var reading))
            {
                misses++;
                return;
            }

            using (reading)
            {
                exchange.Publish(exchange.Rent(Width, Height));
                exchange.Publish(exchange.Rent(Width, Height));
                sequence = reading.Sequence;
            }
        }

        exchange.Publish(exchange.Rent(Width, Height));
        for (int i = 0; i < 10; i++)
        {
            Cycle();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Cycle();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, misses);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AfterAResize_BuffersOfTheOldSizeAreLetGo()
    {
        using var exchange = new WgcFrameExchange();
        var old = new HashSet<WgcFrameExchange.FrameBuffer> { PublishFilled(exchange, 1), PublishFilled(exchange, 2) };
        Assert.True(exchange.TryRead(0, out var reading)); // one of the old size is still being read
        old.Add(PublishFilled(exchange, 3));

        var resized = exchange.Rent(8, 2);
        Assert.DoesNotContain(resized, old);
        Assert.Equal((8, 2), (resized.Width, resized.Height));
        Fill(resized, 4);
        exchange.Publish(resized);
        reading.Dispose();

        Assert.True(exchange.TryRead(0, out var frame));
        using (frame)
        {
            Assert.Equal((8, 2), (frame.Width, frame.Height));
            Assert.All(Pixels(frame), value => Assert.Equal(4, value));
        }

        for (int i = 0; i < 4; i++)
        {
            Assert.DoesNotContain(exchange.Rent(Width, Height), old);
        }
    }

    [Fact]
    public unsafe void CopyingTheLatest_PacksItsRowsTightly()
    {
        using var exchange = new WgcFrameExchange();
        var buffer = exchange.Rent(13, 3); // 52 bytes a row, 64 apart
        var expected = new byte[13 * 4 * 3];
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = (byte)(i % 251);
        }

        for (int row = 0; row < 3; row++)
        {
            expected.AsSpan(row * 52, 52).CopyTo(new Span<byte>((byte*)buffer.Pixels + row * buffer.Stride, 52));
        }

        exchange.Publish(buffer);
        byte[]? copy = null;

        Assert.True(exchange.TryCopyLatest(ref copy, out int width, out int height));
        Assert.Equal((13, 3), (width, height));
        Assert.Equal(expected, copy);

        byte[] first = copy!;
        Assert.True(exchange.TryCopyLatest(ref copy, out _, out _));
        Assert.Same(first, copy); // big enough: used again
    }

    [Fact]
    public void OneLeaseAtATime()
    {
        using var exchange = new WgcFrameExchange();
        PublishFilled(exchange, 1);
        Assert.True(exchange.TryRead(0, out var first));
        PublishFilled(exchange, 2);

        Assert.Throws<InvalidOperationException>(() => exchange.TryRead(0, out _));

        first.Dispose();
        Assert.True(exchange.TryRead(0, out var second));
        second.Dispose();
    }

    [Fact]
    public void DisposingALeaseTwice_DoesNotReleaseALaterOne()
    {
        using var exchange = new WgcFrameExchange();
        PublishFilled(exchange, 1);
        Assert.True(exchange.TryRead(0, out var first));
        var stale = first; // a copy, as a struct lease can be
        first.Dispose();

        var read = PublishFilled(exchange, 2);
        Assert.True(exchange.TryRead(1, out var second));
        stale.Dispose();

        for (int i = 0; i < 6; i++)
        {
            Assert.NotEqual(read, PublishFilled(exchange, (byte)(i + 3)));
        }

        Assert.All(Pixels(second), value => Assert.Equal(2, value));
        second.Dispose();
    }

    [Fact]
    public void AfterDispose_NothingIsReadOrKept_ButAFrameBeingReadStaysValid()
    {
        var exchange = new WgcFrameExchange();
        PublishFilled(exchange, 5);
        Assert.True(exchange.TryRead(0, out var reading));

        exchange.Dispose();
        PublishFilled(exchange, 6); // a readback that finished after the capture stopped
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        byte[]? copy = null;
        Assert.False(exchange.TryRead(0, out _));
        Assert.False(exchange.TryCopyLatest(ref copy, out _, out _));
        Assert.All(Pixels(reading), value => Assert.Equal(5, value));
        reading.Dispose();
        reading.Dispose();
    }

    [Fact]
    public async Task UnderLoad_TheEncoderNeverReadsAFrameWhileItIsWritten()
    {
        const int Frames = 20_000;
        const int FrameWidth = 64;
        const int FrameHeight = 32;
        using var exchange = new WgcFrameExchange();
        var buffers = new HashSet<WgcFrameExchange.FrameBuffer>();
        long reads = 0;
        int torn = 0;
        int outOfOrder = 0;

        // Each frame is filled with its own sequence number, so a write into a frame being read shows as mixed values.
        var readback = OwnThread.Run(() =>
        {
            for (int i = 1; i <= Frames; i++)
            {
                var buffer = exchange.Rent(FrameWidth, FrameHeight);
                buffers.Add(buffer);
                Fill(buffer, (byte)i); // one writer: this frame's sequence number is i
                exchange.Publish(buffer);
            }
        });

        // Reads in place as the single-window encoder does, and copies the latest as the multi-window one does.
        var encoder = OwnThread.Run(() =>
        {
            long sequence = 0;
            byte[]? copy = null;
            while (!readback.IsCompleted || exchange.Sequence > sequence)
            {
                if (exchange.TryCopyLatest(ref copy, out int width, out int height)
                    && copy.AsSpan(0, width * height * 4).IndexOfAnyExcept(copy![0]) >= 0)
                {
                    torn++;
                }

                if (!exchange.TryRead(sequence, out var frame))
                {
                    Thread.SpinWait(20);
                    continue;
                }

                using (frame)
                {
                    if (frame.Sequence <= sequence)
                    {
                        outOfOrder++;
                    }

                    Thread.SpinWait(50); // give the readback time to come round to this buffer, if it could
                    if (!IsFilledWith(frame, (byte)frame.Sequence))
                    {
                        torn++;
                    }

                    sequence = frame.Sequence;
                    reads++;
                }
            }
        });

        await Task.WhenAll(readback, encoder).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, torn);
        Assert.Equal(0, outOfOrder);
        Assert.True(reads > 0);
        Assert.InRange(buffers.Count, 1, 3);
    }

    private static WgcFrameExchange.FrameBuffer PublishFilled(WgcFrameExchange exchange, byte value)
    {
        var buffer = exchange.Rent(Width, Height);
        Fill(buffer, value);
        exchange.Publish(buffer);
        return buffer;
    }

    private static unsafe void Fill(WgcFrameExchange.FrameBuffer buffer, byte value)
    {
        for (int row = 0; row < buffer.Height; row++)
        {
            new Span<byte>((byte*)buffer.Pixels + (long)row * buffer.Stride, buffer.Width * 4).Fill(value);
        }
    }

    private static unsafe byte[] Pixels(in WgcFrameExchange.FrameLease frame)
    {
        int rowBytes = frame.Width * 4;
        var pixels = new byte[rowBytes * frame.Height];
        for (int row = 0; row < frame.Height; row++)
        {
            new ReadOnlySpan<byte>((byte*)frame.Pixels + (long)row * frame.Stride, rowBytes).CopyTo(pixels.AsSpan(row * rowBytes));
        }

        return pixels;
    }

    private static unsafe bool IsFilledWith(in WgcFrameExchange.FrameLease frame, byte value)
    {
        for (int row = 0; row < frame.Height; row++)
        {
            var span = new ReadOnlySpan<byte>((byte*)frame.Pixels + (long)row * frame.Stride, frame.Width * 4);
            if (span.IndexOfAnyExcept(value) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
#endif
