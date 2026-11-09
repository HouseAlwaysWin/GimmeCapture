using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

/// <summary>
/// The stretches of a video a quality compare encodes: what is found where, one encode at a time with the latest request
/// winning, what is kept, and how much is encoded ahead of a playback. The encoder here only writes a file.
/// </summary>
public sealed class CompareSampleWindowsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AWindow_IsFoundForATimeInIt_ButPlaysOnlyWithSomethingLeftInIt()
    {
        var encoder = new FakeEncoder();
        using var windows = Opened(100, encoder);

        CompareSampleWindow? window = await windows.AroundAsync(40).WaitAsync(Patience);

        Assert.NotNull(window);
        Assert.Equal(38.5, window.Start, 3); // a quarter of the short window before the time
        Assert.Equal(CompareSampleWindows.ShortSeconds, window.Length, 3);
        Assert.Same(window, windows.PlayableAt(40));
        Assert.Same(window, windows.PlayableAt(38.5));
        Assert.Null(windows.PlayableAt(38));
        Assert.Null(windows.PlayableAt(window.End)); // its last moment: the next window is the one to play
        Assert.Same(window, await windows.AroundAsync(window.End).WaitAsync(Patience)); // though it holds that frame
        Assert.Single(encoder.Asked);
    }

    [Fact]
    public async Task AskingForAPlaceBeingEncoded_WaitsForThatEncode()
    {
        var encoder = new FakeEncoder { Held = true };
        using var windows = Opened(100, encoder);

        Task<CompareSampleWindow?> first = windows.AroundAsync(40);
        Task<CompareSampleWindow?> around = windows.AroundAsync(41);
        Task<CompareSampleWindow?> toPlay = windows.ToPlayFromAsync(42);
        encoder.Release();

        CompareSampleWindow? window = await first.WaitAsync(Patience);
        Assert.Same(window, await around.WaitAsync(Patience));
        Assert.Same(window, await toPlay.WaitAsync(Patience));
        Assert.Single(encoder.Asked);
    }

    [Fact]
    public async Task AskingForAnotherPlace_CancelsTheEncodeUnderWay_AndRunsAfterIt()
    {
        var encoder = new FakeEncoder { Held = true };
        using var windows = Opened(100, encoder);

        Task<CompareSampleWindow?> first = windows.AroundAsync(10);
        await encoder.WaitForEncodes(1);
        Task<CompareSampleWindow?> second = windows.AroundAsync(50);

        Assert.Null(await first.WaitAsync(Patience)); // cancelled while the encoder held it
        encoder.Release();
        CompareSampleWindow? window = await second.WaitAsync(Patience);

        Assert.NotNull(window);
        Assert.Equal(48.5, window.Start, 3);
        Assert.Equal(0, encoder.MostAtOnceOverOne);
        Assert.Null(windows.PlayableAt(10));
    }

    [Fact]
    public async Task ToPlayFrom_StartsAtTheTimeAskedFor()
    {
        var encoder = new FakeEncoder();
        using var windows = Opened(100, encoder);

        CompareSampleWindow? window = await windows.ToPlayFromAsync(40).WaitAsync(Patience);

        Assert.NotNull(window);
        Assert.Equal(40, window.Start, 3);
        Assert.Equal(CompareSampleWindows.ShortSeconds, window.Length, 3);
    }

    [Fact]
    public async Task EncodingAhead_StartsWhereTheWindowEnds_AndFindsItAsTheNextToPlay()
    {
        var encoder = new FakeEncoder();
        using var windows = Opened(100, encoder);
        CompareSampleWindow? playing = await windows.ToPlayFromAsync(10).WaitAsync(Patience);
        Assert.NotNull(playing);

        windows.EncodeAhead(playing.End, playing.Length);
        CompareSampleWindow? next = await windows.ToPlayFromAsync(playing.End).WaitAsync(Patience);

        Assert.NotNull(next);
        Assert.Equal(playing.End, next.Start, 3);
        Assert.Equal(2, encoder.Asked.Count);
        Assert.Same(next, windows.PlayableAt(playing.End));
    }

    [Fact]
    public async Task EncodingAhead_WhatIsEncodedOrUnderWay_EncodesNothingMore()
    {
        var encoder = new FakeEncoder();
        using var windows = Opened(100, encoder);
        CompareSampleWindow? playing = await windows.ToPlayFromAsync(10).WaitAsync(Patience);
        Assert.NotNull(playing);

        encoder.Held = true;
        windows.EncodeAhead(playing.End, playing.Length);
        windows.EncodeAhead(playing.End, 1); // a pause and a play while it is encoded, with another length
        await encoder.WaitForEncodes(2);
        encoder.Release();
        await windows.ToPlayFromAsync(playing.End).WaitAsync(Patience);

        windows.EncodeAhead(playing.End, playing.Length);
        Assert.Equal(2, encoder.Asked.Count);
    }

    [Fact]
    public async Task EncodingAhead_StopsAtTheEndOfTheVideo()
    {
        var encoder = new FakeEncoder();
        using var windows = Opened(20, encoder);
        CompareSampleWindow? playing = await windows.ToPlayFromAsync(10).WaitAsync(Patience);
        Assert.NotNull(playing);

        windows.EncodeAhead(16, 30); // 4 s of video left
        CompareSampleWindow? last = await windows.ToPlayFromAsync(16).WaitAsync(Patience);

        Assert.NotNull(last);
        Assert.Equal(16, last.Start, 3);
        Assert.Equal(4, last.Length, 3);

        int asked = encoder.Asked.Count;
        windows.EncodeAhead(20, 4); // nothing after the end
        Assert.Equal(asked, encoder.Asked.Count);
    }

    [Theory]
    [InlineData(4.5, 2.5, 9)]        // a fast encoder: what is ready before the window's end, with a margin
    [InlineData(20, 10, 30)]         // no longer than the longest
    [InlineData(6, 0.6, 6)]          // slower than the picture: a short window, so that the picture stops briefly
    [InlineData(6, 0, 6)]            // no encode timed yet
    public void AheadSeconds_FollowTheEncodersSpeed(double toPlay, double speed, double expected) =>
        Assert.Equal(expected, CompareSampleWindows.AheadSeconds(toPlay, speed), 3);

    [Fact]
    public async Task OnlyTheNewestWindowsAreKept()
    {
        var encoder = new FakeEncoder();
        using var windows = Opened(1000, encoder);
        var made = new List<CompareSampleWindow>();
        for (int i = 0; i < CompareSampleWindows.KeptWindows + 2; i++)
        {
            made.Add((await windows.ToPlayFromAsync(i * 100).WaitAsync(Patience))!);
        }

        Assert.All(made.Take(2), window => Assert.False(File.Exists(window.Path)));
        Assert.All(made.Skip(2), window => Assert.True(File.Exists(window.Path)));
        Assert.Null(windows.PlayableAt(made[0].Start));
        Assert.Same(made[^1], windows.PlayableAt(made[^1].Start));
    }

    [Fact]
    public async Task AFailedEncode_GivesNoWindow_AndLeavesNoFile()
    {
        var encoder = new FakeEncoder { Fails = true };
        using var windows = Opened(100, encoder);

        Assert.Null(await windows.ToPlayFromAsync(10).WaitAsync(Patience));
        Assert.Null(windows.PlayableAt(10));
        Assert.All(encoder.Paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task NothingIsEncoded_BeforeTheFolderIsMade()
    {
        var encoder = new FakeEncoder();
        using var windows = new CompareSampleWindows(100, encoder.Encode);

        Assert.Null(await windows.AroundAsync(10).WaitAsync(Patience));
        Assert.Empty(encoder.Asked);
    }

    [Fact]
    public async Task Disposing_StopsTheEncode_AndRemovesTheFolder()
    {
        var encoder = new FakeEncoder();
        var windows = Opened(100, encoder);
        CompareSampleWindow? first = await windows.AroundAsync(10).WaitAsync(Patience);
        Assert.NotNull(first);
        string folder = Path.GetDirectoryName(first.Path)!;

        encoder.Held = true;
        Task<CompareSampleWindow?> underWay = windows.AroundAsync(50);
        await encoder.WaitForEncodes(2);
        windows.Dispose();

        Assert.Null(await underWay.WaitAsync(Patience));
        DateTime deadline = DateTime.UtcNow.Add(Patience);
        while (Directory.Exists(folder))
        {
            Assert.True(DateTime.UtcNow < deadline, "The folder is still there.");
            await OwnThread.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.Null(await windows.AroundAsync(80).WaitAsync(Patience));
        Assert.Equal(2, encoder.Asked.Count);
    }

    private static CompareSampleWindows Opened(double seconds, FakeEncoder encoder)
    {
        var windows = new CompareSampleWindows(seconds, encoder.Encode);
        windows.Open();
        return windows;
    }

    private sealed class FakeEncoder
    {
        private readonly object _lock = new();
        private readonly List<(double Start, double Length)> _asked = [];
        private readonly List<string> _paths = [];
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public bool Held { get; set; }

        public bool Fails { get; init; }

        public int MostAtOnceOverOne { get; private set; }

        public IReadOnlyList<(double Start, double Length)> Asked
        {
            get
            {
                lock (_lock)
                {
                    return _asked.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_lock)
                {
                    return _paths.ToArray();
                }
            }
        }

        public void Release()
        {
            Held = false;
            _release.TrySetResult();
        }

        public async Task WaitForEncodes(int count)
        {
            DateTime deadline = DateTime.UtcNow.Add(Patience);
            while (Asked.Count < count)
            {
                Assert.True(DateTime.UtcNow < deadline, "The encode did not start.");
                await OwnThread.Delay(TimeSpan.FromMilliseconds(2));
            }
        }

        public bool Encode(double start, double length, string path, CancellationToken ct)
        {
            lock (_lock)
            {
                _asked.Add((start, length));
                _paths.Add(path);
                if (++_running > 1)
                {
                    MostAtOnceOverOne = _running - 1;
                }
            }

            try
            {
                if (Held)
                {
                    _release.Task.Wait(ct);
                }

                if (Fails)
                {
                    File.WriteAllBytes(path, []);
                    return false;
                }

                File.WriteAllText(path, "a window");
                return true;
            }
            finally
            {
                lock (_lock)
                {
                    _running--;
                }
            }
        }
    }
}
