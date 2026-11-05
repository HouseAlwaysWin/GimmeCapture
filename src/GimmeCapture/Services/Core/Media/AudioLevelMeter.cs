using System;
using System.Threading;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media;

/// <summary>Reads audio peak levels for <see cref="AudioLevelMeter"/>, on the meter's thread only.</summary>
public interface IAudioLevelReader : IDisposable
{
    /// <summary>The output's peak, and a microphone's when asked; false, with both 0, when they cannot be read.</summary>
    bool TryRead(bool microphone, string? microphoneDeviceId, out double output, out double input);
}

/// <summary>Which microphone to meter, if any: the one the recording takes, when it takes one.</summary>
internal readonly record struct AudioLevelSources(bool Microphone, string? MicrophoneDeviceId);

/// <summary>
/// The levels the Record-mode toolbar shows, read on a thread of its own every <see cref="Interval"/> while it runs, which
/// is all the while the overlay is in Record mode, a recording included, and handed on only when one has changed. The
/// overlay's view model read them on the UI thread from a DispatcherTimer: in silence 63 UI-thread wakeups and 47
/// property changes a second, and the first reading's 58 ms of audio-device set-up on the UI thread
/// (<c>RecordMeterIdleProbeTests</c>).
/// </summary>
internal sealed class AudioLevelMeter
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(120);

    private readonly Func<AudioLevelSources> _sources;
    private readonly Action<double, double> _changed;
    private readonly Func<IAudioLevelReader> _createReader;
    private readonly object _gate = new();
    private ManualResetEventSlim? _stop;

    /// <param name="sources">Asked at every reading, on the meter's thread, which microphone to meter.</param>
    /// <param name="changed">Given the output's and the microphone's level, rounded to hundredths, when either has
    /// changed since the last it was given; on the meter's thread.</param>
    internal AudioLevelMeter(
        Func<AudioLevelSources> sources, Action<double, double> changed, Func<IAudioLevelReader>? createReader = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _createReader = createReader ?? (static () => new AudioLevelMonitorService());
    }

    internal bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _stop != null;
            }
        }
    }

    /// <summary>Starts reading, at once and then every <see cref="Interval"/>; nothing when it already reads.</summary>
    internal void Start()
    {
        lock (_gate)
        {
            if (_stop != null)
            {
                return;
            }

            var stop = new ManualResetEventSlim(initialState: false, spinCount: 0);
            _stop = stop;
            new Thread(() => Run(stop)) { IsBackground = true, Name = "Audio level meter" }.Start();
        }
    }

    /// <summary>
    /// Stops reading without waiting for the thread, which lets its devices go as it ends: a reading stuck in the audio
    /// service must not hold up the caller, the UI thread.
    /// </summary>
    internal void Stop()
    {
        lock (_gate)
        {
            _stop?.Set();
            _stop = null;
        }
    }

    /// <summary>
    /// One reading, handed on when it differs from <paramref name="last"/>, the levels last handed on (negative before
    /// the first). Returns what has now been handed on last.
    /// </summary>
    internal (double Output, double Input) Read(IAudioLevelReader reader, (double Output, double Input) last)
    {
        var sources = _sources();
        double output = 0, input = 0;
        if (reader.TryRead(sources.Microphone, sources.MicrophoneDeviceId, out double outputPeak, out double inputPeak))
        {
            output = Round(outputPeak);
            input = sources.Microphone ? Round(inputPeak) : 0;
        }

        if (output == last.Output && input == last.Input)
        {
            return last;
        }

        _changed(output, input);
        return (output, input);
    }

    // Hundredths: finer jitter is not worth redrawing the meter for.
    private static double Round(double peak) => Math.Round(Math.Clamp(peak, 0, 1), 2);

    private void Run(ManualResetEventSlim stop)
    {
        IAudioLevelReader? reader = null;
        try
        {
            // The devices' COM objects are made here, on this thread, the first time.
            reader = _createReader();
            var last = (Output: -1.0, Input: -1.0);
            do
            {
                last = Read(reader, last);
            }
            while (!stop.Wait(Interval));
        }
        catch (Exception ex)
        {
            // A meter that fails shows nothing; the recording goes on. At exit the dispatcher it posts to is gone.
            AppLog.Debug("AudioLevelMeter", ex);
        }
        finally
        {
            reader?.Dispose();
            stop.Dispose();
        }
    }
}
