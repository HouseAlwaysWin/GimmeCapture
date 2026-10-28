using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// The Compress queue's per-file state map is saved on every item edit. The writer takes those saves off the UI
// thread, folds a burst of them into one write, never runs two writes at once, and writes what is left on Flush.
public class CompressItemStateWriterTests
{
    private static Dictionary<string, CompressItemState> Map(params (string Path, int Rotation)[] entries) =>
        entries.ToDictionary(e => e.Path, e => new CompressItemState { Rotation = e.Rotation }, StringComparer.OrdinalIgnoreCase);

    // Records what is written, and how many writes ran at once.
    private sealed class RecordingWrite
    {
        private readonly object _lock = new();
        private readonly List<IReadOnlyDictionary<string, CompressItemState>> _writes = new();
        private readonly TimeSpan _duration;
        private int _running;

        public RecordingWrite(TimeSpan duration = default) => _duration = duration;

        public int MostAtOnce { get; private set; }

        public IReadOnlyList<IReadOnlyDictionary<string, CompressItemState>> Writes
        {
            get
            {
                lock (_lock)
                {
                    return _writes.ToList();
                }
            }
        }

        public void Write(IReadOnlyDictionary<string, CompressItemState> states)
        {
            int running = Interlocked.Increment(ref _running);
            lock (_lock)
            {
                MostAtOnce = Math.Max(MostAtOnce, running);
            }

            if (_duration > TimeSpan.Zero)
            {
                Thread.Sleep(_duration);
            }

            lock (_lock)
            {
                _writes.Add(states);
            }

            Interlocked.Decrement(ref _running);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the background write");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ABurstOfSaves_IsOneWriteOfTheLastMap()
    {
        var record = new RecordingWrite();
        var writer = new CompressItemStateWriter(record.Write, TimeSpan.FromMilliseconds(200));

        for (int rotation = 0; rotation < 8; rotation++)
        {
            writer.Save(Map(("a.mp4", rotation)));
        }

        await WaitUntilAsync(() => record.Writes.Count > 0);
        await Task.Delay(300); // long enough for a second write, were there one

        IReadOnlyDictionary<string, CompressItemState> written = Assert.Single(record.Writes);
        Assert.Equal(7, written["a.mp4"].Rotation);
    }

    [Fact]
    public void Save_HandsTheWriteToAnotherThread_AndDoesNotWaitForIt()
    {
        using var release = new ManualResetEventSlim();
        int writeThread = 0;
        var writer = new CompressItemStateWriter(_ =>
        {
            Volatile.Write(ref writeThread, Environment.CurrentManagedThreadId);
            release.Wait(TimeSpan.FromSeconds(10)); // a slow disk
        }, TimeSpan.Zero);

        // The caller stands in for the UI thread. It stays alive until the end, so no other thread can take its id.
        int saveThread = 0;
        bool saveReturned = false;
        var caller = new Thread(() =>
        {
            saveThread = Environment.CurrentManagedThreadId;
            writer.Save(Map(("a.mp4", 90)));
            Volatile.Write(ref saveReturned, true);
            release.Wait(TimeSpan.FromSeconds(10));
        });
        caller.Start();

        try
        {
            // Were the write done inside Save, it would block on the slow disk and Save would not return.
            Assert.True(SpinWait.SpinUntil(
                () => Volatile.Read(ref saveReturned) && Volatile.Read(ref writeThread) != 0, TimeSpan.FromSeconds(10)));
            Assert.NotEqual(saveThread, writeThread);
        }
        finally
        {
            release.Set();
            caller.Join();
        }
    }

    [Fact]
    public async Task Save_CopiesTheMap_SoLaterChangesWaitForTheirOwnSave()
    {
        var record = new RecordingWrite();
        var writer = new CompressItemStateWriter(record.Write, TimeSpan.FromMilliseconds(100));
        Dictionary<string, CompressItemState> states = Map(("a.mp4", 90));

        writer.Save(states);
        states["b.mp4"] = new CompressItemState { Rotation = 180 }; // the view model's map keeps changing
        states.Remove("a.mp4");

        await WaitUntilAsync(() => record.Writes.Count == 1);
        IReadOnlyDictionary<string, CompressItemState> written = record.Writes[0];
        Assert.Equal(90, written["a.mp4"].Rotation);
        Assert.False(written.ContainsKey("b.mp4"));
    }

    [Fact]
    public async Task SavesDuringAWrite_NeverStartASecondWriteAtOnce_AndTheLastOneLands()
    {
        var record = new RecordingWrite(TimeSpan.FromMilliseconds(80));
        var writer = new CompressItemStateWriter(record.Write, TimeSpan.Zero);

        for (int rotation = 1; rotation <= 20; rotation++)
        {
            writer.Save(Map(("a.mp4", rotation)));
            await Task.Delay(15);
        }

        await WaitUntilAsync(() => record.Writes.Count > 0 && record.Writes[^1]["a.mp4"].Rotation == 20);
        Assert.Equal(1, record.MostAtOnce);

        // Each write carries a newer map than the one before it.
        int[] rotations = record.Writes.Select(w => w["a.mp4"].Rotation).ToArray();
        Assert.Equal(rotations.OrderBy(r => r).ToArray(), rotations);
        Assert.Equal(rotations.Distinct().Count(), rotations.Length);
    }

    [Fact]
    public void Flush_WritesTheWaitingMapAtOnce()
    {
        var record = new RecordingWrite();
        var writer = new CompressItemStateWriter(record.Write, TimeSpan.FromSeconds(30));

        writer.Save(Map(("a.mp4", 270)));
        writer.Flush();

        IReadOnlyDictionary<string, CompressItemState> written = Assert.Single(record.Writes);
        Assert.Equal(270, written["a.mp4"].Rotation);
    }

    [Fact]
    public void Flush_WithNothingWaiting_WritesNothing()
    {
        var record = new RecordingWrite();
        var writer = new CompressItemStateWriter(record.Write, TimeSpan.FromSeconds(30));

        writer.Flush();

        Assert.Empty(record.Writes);
    }

    [Fact]
    public async Task ASaveAfterAFlush_IsStillWritten()
    {
        var record = new RecordingWrite();
        var writer = new CompressItemStateWriter(record.Write, TimeSpan.FromMilliseconds(50));

        writer.Save(Map(("a.mp4", 90)));
        writer.Flush();
        writer.Save(Map(("a.mp4", 180)));

        await WaitUntilAsync(() => record.Writes.Count == 2);
        Assert.Equal(180, record.Writes[1]["a.mp4"].Rotation);
    }

    [Fact]
    public void WrittenFile_LoadsBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", nameof(CompressItemStateWriterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string video = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(video, new byte[] { 0 }); // Load drops the entries of files that are gone
            string file = Path.Combine(dir, "states", "compress-item-states.json");
            var writer = new CompressItemStateWriter(
                states => CompressItemStateService.Write(file, states), TimeSpan.FromSeconds(30));

            writer.Save(new Dictionary<string, CompressItemState>
            {
                [video] = new CompressItemState { Rotation = 90, OutputName = "renamed", Done = true },
                [Path.Combine(dir, "gone.mp4")] = new CompressItemState { Rotation = 180 },
            });
            writer.Flush();

            Dictionary<string, CompressItemState> loaded = CompressItemStateService.Load(file);
            CompressItemState state = Assert.Single(loaded).Value;
            Assert.Equal(90, state.Rotation);
            Assert.Equal("renamed", state.OutputName);
            Assert.True(state.Done);
            Assert.True(loaded.ContainsKey(video.ToUpperInvariant())); // keyed case-insensitively, as before
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
