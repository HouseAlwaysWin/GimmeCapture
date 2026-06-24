using ReactiveUI;

namespace GimmeCapture.ViewModels.Floating;

/// <summary>
/// One block on the video timeline strip — a kept segment the user can click to select (then delete).
/// A thin view over a <see cref="GimmeCapture.Models.VideoEditSegment"/>; positions are layout-driven by
/// the ItemsControl, so this only carries the index, a human label, and selection state.
/// </summary>
public sealed class SegmentBlockViewModel : ReactiveObject
{
    public SegmentBlockViewModel(int index, string label)
    {
        Index = index;
        _label = label;
    }

    public int Index { get; }

    private string _label;
    public string Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
