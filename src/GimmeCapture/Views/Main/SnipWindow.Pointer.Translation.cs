using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using System;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow
{
    private bool TryHandleTranslationPointerPressed(Point point, PointerPressedEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsTranslationMode) return false;

        var props = e.GetCurrentPoint(this).Properties;
        var sourceControl = e.Source as Control;

        bool hasAnyBox = _viewModel.UserSelections.AsValueEnumerable()
            .Any(s => !s.IsAudioPanel && s.Bounds.Width > 5 && s.Bounds.Height > 5);
        bool selModDown = IsTranslationSelectionModifierDownForPointer();

        if (props.IsRightButtonPressed)
        {
            return HandleTranslationRightClick(point, sourceControl, e);
        }

        if (props.IsLeftButtonPressed)
        {
            // Hold configured modifier (or none) — Win32 region uses full hit while modifier is down.
            if (!selModDown)
                return false;

            if (_viewModel.IsTranslationOcrSearchActive && _viewModel.TryMaterializeTranslationOcrCandidateAtPoint(point))
            {
                e.Handled = true;
                return true;
            }

            bool isAdditional = hasAnyBox;
            _viewModel.CurrentTranslationTool = isAdditional ? TranslationTool.Multi : TranslationTool.Single;
            _pointerState = PointerInteractionState.TranslationSelecting;
            _translationSelectionStart = point;
            _currentTranslationSelection = new UserSelectionRect { Bounds = new Rect(point, new Size(0, 0)) };
            _viewModel.UserSelections.Add(_currentTranslationSelection);
            _viewModel.UpdateMask();
            e.Pointer.Capture(this);
            e.Handled = true;
            return true;
        }

        return false;
    }

    private bool HandleTranslationRightClick(Point point, Control? sourceControl, PointerPressedEventArgs e)
    {
        if (_viewModel == null) return false;

        System.Diagnostics.Debug.WriteLine($"[TranslationRightClick] PointerPressed at {point}. Source: {sourceControl?.GetType().Name}, Name: {sourceControl?.Name}, DataContext: {sourceControl?.DataContext?.GetType().Name}");

        var selRect = ResolveTranslationSelectionFromVisualTree(sourceControl);

        if (selRect == null && _viewModel.UserSelections != null)
        {
            for (int i = _viewModel.UserSelections.Count - 1; i >= 0; i--)
            {
                var box = _viewModel.UserSelections[i];
                if (box.Bounds.Contains(point))
                {
                    System.Diagnostics.Debug.WriteLine($"[TranslationRightClick] Coordinate hit test succeeded for box {i}.");
                    selRect = box;
                    break;
                }
            }
        }

        System.Diagnostics.Debug.WriteLine($"[TranslationRightClick] Final selRect resolved: {selRect != null}");

        var vm = _viewModel;
        if (selRect != null && vm != null) return false;

        return false;
    }

    private bool TryHandleTranslationPointerMoved(Point currentPoint, PointerEventArgs e)
    {
        if (_viewModel == null) return false;

        if (_pointerState == PointerInteractionState.ResizingTranslationBox && _resizingTranslationItem != null)
        {
            var dx = currentPoint.X - _translationResizeStartPoint.X;
            var dy = currentPoint.Y - _translationResizeStartPoint.Y;

            double newX = _originalTranslationRect.X;
            double newY = _originalTranslationRect.Y;
            double newW = _originalTranslationRect.Width;
            double newH = _originalTranslationRect.Height;

            switch (_translationResizeDirection)
            {
                case ResizeDirection.TopLeft:     newX += dx; newY += dy; newW -= dx; newH -= dy; break;
                case ResizeDirection.TopRight:    newY += dy; newW += dx; newH -= dy; break;
                case ResizeDirection.BottomLeft:  newX += dx; newW -= dx; newH += dy; break;
                case ResizeDirection.BottomRight: newW += dx; newH += dy; break;
                case ResizeDirection.Top:         newY += dy; newH -= dy; break;
                case ResizeDirection.Bottom:      newH += dy; break;
                case ResizeDirection.Left:        newX += dx; newW -= dx; break;
                case ResizeDirection.Right:       newW += dx; break;
            }

            if (newW < 40) newW = 40;
            if (newH < 40) newH = 40;

            _resizingTranslationItem.Bounds = new Rect(newX, newY, Math.Max(0, newW), Math.Max(0, newH));
            _viewModel.UpdateMask();
            e.Handled = true;
            return true;
        }

        if (_pointerState == PointerInteractionState.MovingTranslationBox && _movingTranslationSelection != null)
        {
            var deltaX = currentPoint.X - _translationMoveStart.X;
            var deltaY = currentPoint.Y - _translationMoveStart.Y;
            _movingTranslationSelection.Bounds = new Rect(
                _originalTranslationBounds.X + deltaX,
                _originalTranslationBounds.Y + deltaY,
                _originalTranslationBounds.Width,
                _originalTranslationBounds.Height);
            return true;
        }

        if (_pointerState == PointerInteractionState.TranslationSelecting && _currentTranslationSelection != null)
        {
            var x = Math.Min(_translationSelectionStart.X, currentPoint.X);
            var y = Math.Min(_translationSelectionStart.Y, currentPoint.Y);
            var width = Math.Abs(currentPoint.X - _translationSelectionStart.X);
            var height = Math.Abs(currentPoint.Y - _translationSelectionStart.Y);
            _currentTranslationSelection.Bounds = new Rect(x, y, width, height);
            return true;
        }

        return false;
    }

    private bool TryHandleTranslationPointerReleased(PointerReleasedEventArgs e)
    {
        if (_pointerState == PointerInteractionState.MovingTranslationBox)
        {
            _pointerState = PointerInteractionState.None;
            _movingTranslationSelection = null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return true;
        }

        if (_pointerState == PointerInteractionState.ResizingTranslationBox)
        {
            _pointerState = PointerInteractionState.None;
            _resizingTranslationItem = null;
            _translationResizeDirection = ResizeDirection.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            return true;
        }

        if (_pointerState == PointerInteractionState.TranslationSelecting)
        {
            _pointerState = PointerInteractionState.None;
            var vm = _viewModel;
            if (_currentTranslationSelection != null)
            {
                if (_currentTranslationSelection.Bounds.Width < 10 || _currentTranslationSelection.Bounds.Height < 5)
                {
                    vm?.UserSelections?.Remove(_currentTranslationSelection);
                }

                vm?.UpdateMask();
                _currentTranslationSelection = null;
            }

            var holdMod = _viewModel?.TranslationSelectionHoldModifier ?? "Ctrl";
            if (!string.Equals(holdMod, "None", StringComparison.OrdinalIgnoreCase) &&
                IsPhysicalModifierLabelDown(holdMod))
                _translationSuppressFullHitUntilSelectionModifierUp = true;
            RequestTranslationWindowRegionRefresh();

            e.Pointer.Capture(null);
            e.Handled = true;
            return true;
        }

        return false;
    }

    private static UserSelectionRect? ResolveTranslationSelectionFromVisualTree(Control? sourceControl)
    {
        var selRect = sourceControl?.DataContext as UserSelectionRect;
        if (selRect != null) return selRect;

        var parent = sourceControl?.GetVisualParent();
        while (parent != null)
        {
            if (parent is Control c && c.DataContext is UserSelectionRect found)
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
}
