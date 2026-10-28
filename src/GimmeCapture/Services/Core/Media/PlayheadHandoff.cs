namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// The playhead position a video's decode thread last reached, waiting for the UI thread to show it. The pin's decoder
/// used to set its CurrentTime on every frame, which raised the change notifications and rebuilt the redaction preview
/// boxes (a collection the view is bound to) off the UI thread. It now records the time here and the throttled UI
/// refresh takes it. Each position carries the playback generation it was decoded for, so one from a playback that a
/// seek has since replaced is never shown.
/// </summary>
internal sealed class PlayheadHandoff
{
    private readonly object _gate = new();
    private double _seconds;
    private int _generation;
    private bool _recorded;
    private bool _taken;

    /// <summary>Decode thread: the playhead reached <paramref name="seconds"/> in playback <paramref name="generation"/>.</summary>
    public void Record(double seconds, int generation)
    {
        lock (_gate)
        {
            _seconds = seconds;
            _generation = generation;
            _recorded = true;
            _taken = false;
        }
    }

    /// <summary>UI thread: the latest position of playback <paramref name="generation"/>, unless it was taken already.</summary>
    public bool TryTake(int generation, out double seconds)
    {
        lock (_gate)
        {
            if (!_recorded || _taken || _generation != generation)
            {
                seconds = 0;
                return false;
            }

            _taken = true;
            seconds = _seconds;
            return true;
        }
    }

    /// <summary>The latest position of playback <paramref name="generation"/>, taken or not.</summary>
    public bool TryPeek(int generation, out double seconds)
    {
        lock (_gate)
        {
            if (!_recorded || _generation != generation)
            {
                seconds = 0;
                return false;
            }

            seconds = _seconds;
            return true;
        }
    }
}
