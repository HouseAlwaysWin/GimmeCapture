namespace GimmeCapture.Services.Core.Media;

/// <summary>Where a preview sound stands, for a picture that keeps time with it.</summary>
internal enum SoundClockState
{
    /// <summary>Its file and output device are still being opened: the sound will start shortly.</summary>
    Pending,

    /// <summary>It plays, and <see cref="SoundClockReading.Seconds"/> says how much of it has.</summary>
    Playing,

    /// <summary>
    /// Nothing to keep time with: it was stopped or replaced, it failed, the clip has no sound, or its output keeps no
    /// clock to read.
    /// </summary>
    Unavailable,
}

/// <summary>
/// One reading of a preview sound's clock (<see cref="AudioPreviewPlayer.ReadClock"/>): while it plays,
/// <see cref="Seconds"/> is how much of it the output device has played since it started.
/// </summary>
internal readonly record struct SoundClockReading(SoundClockState State, double Seconds)
{
    public static SoundClockReading Pending { get; } = new(SoundClockState.Pending, 0);

    public static SoundClockReading Unavailable { get; } = new(SoundClockState.Unavailable, 0);

    public static SoundClockReading At(double seconds) => new(SoundClockState.Playing, seconds);
}
