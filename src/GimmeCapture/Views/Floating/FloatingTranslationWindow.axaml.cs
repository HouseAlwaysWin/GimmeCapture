using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using GimmeCapture.ViewModels.Floating;
using System;

namespace GimmeCapture.Views.Floating;

public partial class FloatingTranslationWindow : FloatingWindowBase
{
    private bool _isDraggingToolbar;
    private Point _toolbarDragStart;

    public FloatingTranslationWindow()
    {
        InitializeComponent();

        // 同步視窗位置到 ViewModel
        PositionChanged += (s, e) =>
        {
            if (DataContext is FloatingTranslationViewModel vm)
            {
                vm.ScreenPosition = Position;
            }
        };

        // 同步 Toolbar 尺寸到 ViewModel
        var toolbar = this.FindControl<Border>("ToolbarBorder");
        if (toolbar != null)
        {
            toolbar.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
            {
                if (DataContext is FloatingTranslationViewModel vm)
                {
                    vm.ToolbarWidth = bounds.Width;
                    vm.ToolbarHeight = bounds.Height;
                }
            });
        }

        // 工具列拖曳
        var dragHandle = this.FindControl<Border>("ToolbarDragHandle");
        if (dragHandle != null)
        {
            dragHandle.PointerPressed += OnToolbarDragStart;
            dragHandle.PointerMoved += OnToolbarDragMove;
            dragHandle.PointerReleased += OnToolbarDragEnd;
        }

        // 工具列本體也可以拖曳
        if (toolbar != null)
        {
            toolbar.PointerPressed += OnToolbarDragStart;
            toolbar.PointerMoved += OnToolbarDragMove;
            toolbar.PointerReleased += OnToolbarDragEnd;
        }
    }

    protected override Control? GetContentControl() => this.FindControl<Image>("PinnedImage");

    protected override Bitmap? GetContentSnapshot() => (DataContext as FloatingTranslationViewModel)?.Image;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingTranslationViewModel vm)
        {
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingTranslationViewModel.Image))
                {
                    SyncWindowSizeToContent();
                }
            };
        }
    }

    private void OnToolbarDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDraggingToolbar = true;
            _toolbarDragStart = e.GetPosition(this);
            e.Pointer.Capture((Control)sender!);
            e.Handled = true;
        }
    }

    private void OnToolbarDragMove(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingToolbar) return;

        var toolbar = this.FindControl<Border>("ToolbarBorder");
        if (toolbar == null) return;

        var currentPos = e.GetPosition(this);
        var delta = currentPos - _toolbarDragStart;

        // 更新工具列 Margin 來移動它
        var currentMargin = toolbar.Margin;
        toolbar.Margin = new Thickness(
            currentMargin.Left + delta.X,
            currentMargin.Top + delta.Y,
            currentMargin.Right - delta.X,
            currentMargin.Bottom - delta.Y);

        _toolbarDragStart = currentPos;
        e.Handled = true;
    }

    private void OnToolbarDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDraggingToolbar)
        {
            _isDraggingToolbar = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
