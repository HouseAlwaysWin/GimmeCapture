using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GimmeCapture.ViewModels;

public partial class SnipWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private Rect _selectionRect;

    [ObservableProperty]
    private bool _isSelecting;

    // TODO: Add logic to confirm selection or cancel
}
