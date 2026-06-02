using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Floating;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Disposables;

namespace GimmeCapture.Views.Floating;

public partial class FloatingTranslationWindow : Window
{
    private readonly CompositeDisposable _itemSubscriptions = new();
    private readonly CompositeDisposable _viewSubscriptions = new();
    private FloatingTranslationLayerViewModel? _viewModel;
    private PointerState _pointerState;
    private TranslationResultItem? _movingItem;
    private TranslationResultItem? _resizingItem;
    private Point _dragStart;
    private Point _resizeStart;
    private Rect _originalBounds;
    private ResizeDirection _resizeDirection;

    private enum PointerState
    {
        None,
        Moving,
        Resizing
    }

    private enum ResizeDirection
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Top,
        Bottom,
        Left,
        Right
    }

    public FloatingTranslationWindow()
    {
        InitializeComponent();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        ContextRequested += OnContextRequested;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshWindowRegion();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _itemSubscriptions.Clear();
        _viewSubscriptions.Clear();
        if (_viewModel != null)
        {
            _viewModel.Items.CollectionChanged -= OnItemsCollectionChanged;
        }

        _viewModel = DataContext as FloatingTranslationLayerViewModel;
        if (_viewModel == null)
        {
            return;
        }

        _viewModel.Items.CollectionChanged += OnItemsCollectionChanged;
        SubscribeToItems();
        this.GetObservable(BoundsProperty).Subscribe(_ => RefreshWindowRegion()).DisposeWith(_viewSubscriptions);
        _viewModel.WhenAnyValue(x => x.ViewportSize).Subscribe(_ => RefreshWindowRegion()).DisposeWith(_viewSubscriptions);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _itemSubscriptions.Dispose();
        _viewSubscriptions.Dispose();
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            Win32Helpers.ClearWindowRegion(hwnd);
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeToItems();
        if (_viewModel?.Items.Count == 0)
        {
            Hide();
        }
        RefreshWindowRegion();
    }

    private void SubscribeToItems()
    {
        _itemSubscriptions.Clear();
        if (_viewModel == null)
        {
            return;
        }

        foreach (var item in _viewModel.Items)
        {
            item.PropertyChanged += OnItemPropertyChanged;
            Disposable.Create(() => item.PropertyChanged -= OnItemPropertyChanged).DisposeWith(_itemSubscriptions);
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranslationResultItem.Bounds)
            or nameof(TranslationResultItem.IsTranslated)
            or nameof(TranslationResultItem.EstimatedTextHeight))
        {
            RefreshWindowRegion();
        }
    }

    private void RefreshWindowRegion()
    {
        if (_viewModel == null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        double scaling = RenderScaling;
        var opaqueRects = new List<Rect>();
        foreach (var item in _viewModel.Items)
        {
            double x = Math.Max(0, (item.Bounds.X - 20) * scaling);
            double y = Math.Max(0, (item.Bounds.Y - 20) * scaling);
            double width = Math.Max(1, (item.Bounds.Width + 40) * scaling);
            double height = Math.Max(1, (item.Bounds.Height + 40) * scaling);
            opaqueRects.Add(new Rect(x, y, width, height));
        }

        opaqueRects.Add(new Rect(0, 0, 1, 1));
        Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaqueRects, null);
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var src = e.Source as Control;
        if (src is SelectableTextBlock || src?.FindAncestorOfType<SelectableTextBlock>() != null)
        {
            return;
        }

        if (ResolveItemFromVisualTree(src) != null)
        {
            return;
        }

        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Control;
        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        var item = ResolveItemFromVisualTree(source);
        if (item == null)
        {
            return;
        }

        if (HasClassInAncestors(source, "Handle"))
        {
            _pointerState = PointerState.Resizing;
            _resizingItem = item;
            _resizeStart = point;
            _originalBounds = item.Bounds;
            _resizeDirection = GetDirectionFromName(ResolveHandleName(source));
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (source is SelectableTextBlock || source?.FindAncestorOfType<SelectableTextBlock>() != null)
        {
            return;
        }

        if (HasClassInAncestors(source, "MoveHandle"))
        {
            _pointerState = PointerState.Moving;
            _movingItem = item;
            _dragStart = point;
            _originalBounds = item.Bounds;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_pointerState == PointerState.Moving && _movingItem != null)
        {
            var dx = point.X - _dragStart.X;
            var dy = point.Y - _dragStart.Y;
            _movingItem.Bounds = new Rect(
                _originalBounds.X + dx,
                _originalBounds.Y + dy,
                _originalBounds.Width,
                _originalBounds.Height);
            e.Handled = true;
            return;
        }

        if (_pointerState == PointerState.Resizing && _resizingItem != null)
        {
            var dx = point.X - _resizeStart.X;
            var dy = point.Y - _resizeStart.Y;

            double newX = _originalBounds.X;
            double newY = _originalBounds.Y;
            double newWidth = _originalBounds.Width;
            double newHeight = _originalBounds.Height;

            switch (_resizeDirection)
            {
                case ResizeDirection.TopLeft:
                    newX += dx; newY += dy; newWidth -= dx; newHeight -= dy; break;
                case ResizeDirection.TopRight:
                    newY += dy; newWidth += dx; newHeight -= dy; break;
                case ResizeDirection.BottomLeft:
                    newX += dx; newWidth -= dx; newHeight += dy; break;
                case ResizeDirection.BottomRight:
                    newWidth += dx; newHeight += dy; break;
                case ResizeDirection.Top:
                    newY += dy; newHeight -= dy; break;
                case ResizeDirection.Bottom:
                    newHeight += dy; break;
                case ResizeDirection.Left:
                    newX += dx; newWidth -= dx; break;
                case ResizeDirection.Right:
                    newWidth += dx; break;
            }

            _resizingItem.Bounds = new Rect(newX, newY, Math.Max(40, newWidth), Math.Max(40, newHeight));
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointerState == PointerState.None)
        {
            return;
        }

        _pointerState = PointerState.None;
        _movingItem = null;
        _resizingItem = null;
        _resizeDirection = ResizeDirection.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private static TranslationResultItem? ResolveItemFromVisualTree(Control? sourceControl)
    {
        var item = sourceControl?.DataContext as TranslationResultItem;
        if (item != null)
        {
            return item;
        }

        var parent = sourceControl?.GetVisualParent();
        while (parent != null)
        {
            if (parent is Control control && control.DataContext is TranslationResultItem found)
            {
                return found;
            }

            parent = parent.GetVisualParent();
        }

        return null;
    }

    private static bool HasClassInAncestors(Control? sourceControl, string className)
    {
        var current = sourceControl;
        while (current != null)
        {
            if (current.Classes.Contains(className))
            {
                return true;
            }

            current = current.GetVisualParent() as Control;
        }

        return false;
    }

    private static string? ResolveHandleName(Control? sourceControl)
    {
        var current = sourceControl;
        while (current != null)
        {
            if (current.Classes.Contains("Handle"))
            {
                return current.Name;
            }

            current = current.GetVisualParent() as Control;
        }

        return null;
    }

    private static ResizeDirection GetDirectionFromName(string? name)
    {
        return name switch
        {
            "HandleTopLeft" => ResizeDirection.TopLeft,
            "HandleTopRight" => ResizeDirection.TopRight,
            "HandleBottomLeft" => ResizeDirection.BottomLeft,
            "HandleBottomRight" => ResizeDirection.BottomRight,
            "HandleTop" => ResizeDirection.Top,
            "HandleBottom" => ResizeDirection.Bottom,
            "HandleLeft" => ResizeDirection.Left,
            "HandleRight" => ResizeDirection.Right,
            _ => ResizeDirection.None
        };
    }
}
