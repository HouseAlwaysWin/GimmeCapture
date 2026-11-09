using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Tests;

// The picture under a drag along a video's timeline. The decodes here finish when the test says so.
public sealed class ScrubFramePumpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class Decodes
    {
        private readonly object _gate = new();
        private readonly List<(double Seconds, CancellationToken Token, TaskCompletionSource<byte[]?> Result)> _calls = [];
        private readonly List<byte> _shown = [];

        public Task<byte[]?> Decode(double seconds, CancellationToken token)
        {
            var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _calls.Add((seconds, token, result));
            }

            return result.Task;
        }

        public void Show(byte[] frame)
        {
            lock (_gate)
            {
                _shown.Add(frame[0]);
            }
        }

        public double[] Asked
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Select(call => call.Seconds).ToArray();
                }
            }
        }

        public byte[] Shown
        {
            get
            {
                lock (_gate)
                {
                    return [.. _shown];
                }
            }
        }

        public CancellationToken TokenOf(int call)
        {
            lock (_gate)
            {
                return _calls[call].Token;
            }
        }

        public void Finish(int call, byte frame)
        {
            TaskCompletionSource<byte[]?> result;
            lock (_gate)
            {
                result = _calls[call].Result;
            }

            result.SetResult([frame]);
        }

        public void Fail(int call)
        {
            TaskCompletionSource<byte[]?> result;
            lock (_gate)
            {
                result = _calls[call].Result;
            }

            result.SetException(new InvalidOperationException("no frame here"));
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await OwnThread.Delay(TimeSpan.FromMilliseconds(2));
        }
    }

    // Lets what has finished run on, for asserting that nothing more happens.
    private static Task Settle() => OwnThread.Delay(TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task TheDecodeUnderWay_IsNotCancelled_AndOnlyTheNewestPositionFollowsIt()
    {
        // Each new position cancelling the decode before it showed nothing until the drag stopped, on a video whose frames
        // take longer to decode than the pointer takes to move.
        var decodes = new Decodes();
        var pump = new ScrubFramePump(decodes.Decode, decodes.Show);

        pump.Request(1);
        await Until(() => decodes.Asked.Length == 1);
        pump.Request(2);
        pump.Request(3);
        await Settle();
        Assert.Equal([1d], decodes.Asked);
        Assert.False(decodes.TokenOf(0).IsCancellationRequested);

        decodes.Finish(0, 10);
        await Until(() => decodes.Asked.Length == 2);
        Assert.Equal([1d, 3d], decodes.Asked);
        Assert.Equal([(byte)10], decodes.Shown);

        decodes.Finish(1, 30);
        await Until(() => decodes.Shown.Length == 2);
        Assert.Equal([(byte)10, (byte)30], decodes.Shown);
    }

    [Fact]
    public async Task Stop_DropsWhatWasAskedFor_AndTheFrameUnderWay()
    {
        var decodes = new Decodes();
        var pump = new ScrubFramePump(decodes.Decode, decodes.Show);
        pump.Request(1);
        await Until(() => decodes.Asked.Length == 1);
        pump.Request(2);

        pump.Stop();

        Assert.True(decodes.TokenOf(0).IsCancellationRequested);
        decodes.Finish(0, 10);
        await Settle();
        Assert.Empty(decodes.Shown);
        Assert.Equal([1d], decodes.Asked);
    }

    [Fact]
    public async Task ARequestAfterAStop_IsDecoded_ThoughTheOldDecodeIsStillFinishing()
    {
        var decodes = new Decodes();
        var pump = new ScrubFramePump(decodes.Decode, decodes.Show);
        pump.Request(1);
        await Until(() => decodes.Asked.Length == 1);

        pump.Stop();
        pump.Request(2);
        decodes.Finish(0, 10);

        await Until(() => decodes.Asked.Length == 2);
        Assert.False(decodes.TokenOf(1).IsCancellationRequested);
        decodes.Finish(1, 20);
        await Until(() => decodes.Shown.Length == 1);
        Assert.Equal([(byte)20], decodes.Shown);
    }

    [Fact]
    public async Task ADecodeThatFails_CostsOneFrame()
    {
        var decodes = new Decodes();
        var pump = new ScrubFramePump(decodes.Decode, decodes.Show);
        pump.Request(1);
        await Until(() => decodes.Asked.Length == 1);
        pump.Request(2);

        decodes.Fail(0);

        await Until(() => decodes.Asked.Length == 2);
        decodes.Finish(1, 20);
        await Until(() => decodes.Shown.Length == 1);
        Assert.Equal([(byte)20], decodes.Shown);

        // And it is still there for the next position.
        pump.Request(3);
        await Until(() => decodes.Asked.Length == 3);
        decodes.Finish(2, 30);
        await Until(() => decodes.Shown.Length == 2);
    }
}
