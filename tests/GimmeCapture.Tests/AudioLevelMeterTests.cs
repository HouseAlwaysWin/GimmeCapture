using System.Diagnostics;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

// The Record-mode meter read its levels on the UI thread from a 120 ms DispatcherTimer and raised every one: in silence
// 63 UI-thread wakeups and 47 property changes a second, its first reading's 58 ms of device set-up on the UI thread.
public sealed class AudioLevelMeterTests
{
    [Fact]
    public void AReading_IsHandedOn_OnlyWhenItChanged_ToTheHundredth()
    {
        var reader = new FakeReader((true, 0.0, 0.0), (true, 0.0, 0.0), (true, 0.123, 0.5), (true, 0.124, 0.5), (true, 0.2, 0.5));
        var handed = new List<(double, double)>();
        var meter = new AudioLevelMeter(() => new AudioLevelSources(true, null), (o, i) => handed.Add((o, i)));

        var last = (Output: -1.0, Input: -1.0);
        for (int i = 0; i < 5; i++)
        {
            last = meter.Read(reader, last);
        }

        Assert.Equal([(0.0, 0.0), (0.12, 0.5), (0.2, 0.5)], handed);
    }

    [Fact]
    public void AMicrophoneTheRecordingDoesNotTake_IsNotRead()
    {
        var reader = new FakeReader((true, 0.3, 0.9));
        var handed = new List<(double, double)>();
        var meter = new AudioLevelMeter(() => new AudioLevelSources(false, "mic-1"), (o, i) => handed.Add((o, i)));

        meter.Read(reader, (-1, -1));

        Assert.Equal([false], reader.MicrophoneAsked);
        Assert.Equal([(0.3, 0.0)], handed);
    }

    [Fact]
    public void AReadingThatFails_ShowsSilence()
    {
        var reader = new FakeReader((false, 0.7, 0.7));
        var handed = new List<(double, double)>();
        var meter = new AudioLevelMeter(() => new AudioLevelSources(true, null), (o, i) => handed.Add((o, i)));

        Assert.Equal((0.0, 0.0), meter.Read(reader, (0.4, 0.4)));
        Assert.Equal([(0.0, 0.0)], handed);
    }

    [Fact]
    public async Task TheMeter_ReadsOnAThreadOfItsOwn_AndLetsItsDevicesGoWhenStopped()
    {
        var reader = new FakeReader((true, 0.5, 0.0));
        var meter = new AudioLevelMeter(() => new AudioLevelSources(false, null), (_, _) => { }, () => reader);

        meter.Start();
        Assert.True(meter.IsRunning);
        await WaitUntil(() => reader.Reads > 0);
        Assert.NotEqual(Environment.CurrentManagedThreadId, reader.ReadOnThread);

        meter.Stop();
        Assert.False(meter.IsRunning);
        await WaitUntil(() => reader.Disposed);
        int reads = reader.Reads;
        await OwnThread.Delay(AudioLevelMeter.Interval * 3);
        Assert.Equal(reads, reader.Reads);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        var giveUp = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(giveUp.Elapsed < TimeSpan.FromSeconds(10), "Timed out.");
            await OwnThread.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    // Gives its readings in turn, the last one over and over.
    private sealed class FakeReader : IAudioLevelReader
    {
        private readonly (bool Ok, double Output, double Input)[] _readings;

        public FakeReader(params (bool Ok, double Output, double Input)[] readings) => _readings = readings;

        public List<bool> MicrophoneAsked { get; } = new();

        public volatile int ReadOnThread;

        public volatile bool Disposed;

        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public bool TryRead(bool microphone, string? microphoneDeviceId, out double output, out double input)
        {
            var reading = _readings[Math.Min(_reads, _readings.Length - 1)];
            lock (MicrophoneAsked)
            {
                MicrophoneAsked.Add(microphone);
            }

            ReadOnThread = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _reads);
            output = reading.Output;
            input = reading.Input;
            return reading.Ok;
        }

        public void Dispose() => Disposed = true;
    }
}
