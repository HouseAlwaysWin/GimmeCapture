using ReactiveUI;

namespace GimmeCapture.ViewModels.Floating;

/// <summary>
/// One block on the video timeline strip — a kept segment the user can click to select (then delete).
/// A thin view over a <see cref="GimmeCapture.Models.VideoEditSegment"/>. It carries its OUTPUT-time
/// extent so the view can lay blocks out proportionally (pixel positions are computed by the view from
/// the strip's width, mirroring the trim thumbs).
/// </summary>
public sealed class SegmentBlockViewModel : ReactiveObject
{
    public SegmentBlockViewModel(int index, string label, double outputStart, double outputDuration)
    {
        Index = index;
        _label = label;
        OutputStart = outputStart;
        OutputDuration = outputDuration;
    }

    public int Index { get; }

    /// <summary>Where this block begins on the OUTPUT timeline (seconds). Drives proportional layout.</summary>
    public double OutputStart { get; }

    /// <summary>This block's length on the OUTPUT timeline (seconds).</summary>
    public double OutputDuration { get; }

    private string _label;
    public string Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    private bool _isKept = true;
    /// <summary>Whether this piece is kept in the output (kept = highlighted; dropped = dimmed).</summary>
    public bool IsKept
    {
        get => _isKept;
        set => this.RaiseAndSetIfChanged(ref _isKept, value);
    }

    private double _speed = 1.0;
    /// <summary>Playback speed for this piece (1.0 = normal). Shown as a badge when ≠ 1.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            this.RaiseAndSetIfChanged(ref _speed, value);
            this.RaisePropertyChanged(nameof(SpeedLabel));
            this.RaisePropertyChanged(nameof(HasCustomSpeed));
        }
    }

    /// <summary>Badge text like "2×" / "0.5×"; empty at normal speed.</summary>
    public string SpeedLabel => System.Math.Abs(_speed - 1.0) < 0.001 ? string.Empty : $"{_speed:0.##}×";

    public bool HasCustomSpeed => System.Math.Abs(_speed - 1.0) >= 0.001;

    // Pixel layout on the strip, computed by the view from the track width (mirrors the trim thumbs).
    private double _pixelLeft;
    public double PixelLeft
    {
        get => _pixelLeft;
        set => this.RaiseAndSetIfChanged(ref _pixelLeft, value);
    }

    private double _pixelWidth = 1;
    public double PixelWidth
    {
        get => _pixelWidth;
        set => this.RaiseAndSetIfChanged(ref _pixelWidth, value);
    }
}
