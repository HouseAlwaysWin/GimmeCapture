using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace GimmeCapture.ViewModels;

public partial class SnipWindowViewModel : ViewModelBase
{
    public enum SnipState { Idle, Detecting, Selecting, Selected }

    [ObservableProperty]
    private SnipState _currentState = SnipState.Idle;

    [ObservableProperty]
    private Rect _selectionRect;

    [ObservableProperty]
    private Geometry _screenGeometry = Geometry.Parse("M0,0 L0,0 0,0 0,0 Z"); // Default empty

    [ObservableProperty]
    private GeometryGroup _maskGeometry = new GeometryGroup();

    [RelayCommand]
    private void Copy() { /* TODO: Implement Copy */ CloseAction?.Invoke(); }

    [RelayCommand]
    private void Save() { /* TODO: Implement Save */ CloseAction?.Invoke(); }

    [RelayCommand]
    private void Close() { CloseAction?.Invoke(); }
    
    public Action? CloseAction { get; set; }
}
