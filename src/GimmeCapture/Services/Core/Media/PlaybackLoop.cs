namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// A stretch of a clip that plays over and over, from <see cref="StartSeconds"/> to <see cref="EndSeconds"/> in the
/// file's own time. A playback may begin inside it; every pass after the first starts at its start. A video pin plays
/// its picture (<see cref="NativeFFmpeg.LibavVideoFramePlayer"/>) and its sound (<see cref="AudioPreviewPlayer"/>)
/// through the same loop, so the two lay their passes out alike and stay together pass after pass.
/// </summary>
internal readonly record struct PlaybackLoop(double StartSeconds, double EndSeconds)
{
    // A loop no longer than this is played once instead: its passes would be no more than a frame or two.
    private const double MinLengthSeconds = 0.05;

    /// <summary>True when the loop is long enough to play over and over; false while its end is unknown (NaN).</summary>
    public bool CanLoop => EndSeconds - StartSeconds > MinLengthSeconds;

    /// <summary>
    /// Where a playback asked to begin at <paramref name="seconds"/> begins: there when that is inside the loop, else at
    /// the loop's start. The picture and the sound both begin where this says.
    /// </summary>
    public double ClampStart(double seconds) => seconds >= StartSeconds && seconds < EndSeconds ? seconds : StartSeconds;
}
