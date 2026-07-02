using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Views.Shared;

/// <summary>
/// The annotation pointer state machine (draw / select / drag-transform / text entry / cursor) shared by
/// the floating Pin windows and the compress 進階影片編輯 editor. Operates directly on the composable
/// <see cref="AnnotationEditorState"/>; the hosting window supplies its content control, frame snapshot,
/// text-input focus, and blocked flag through constructor delegates. Extracted verbatim from
/// <c>FloatingWindowBase</c> so Pin behavior is unchanged.
/// </summary>
public sealed class AnnotationInputController
{
    private readonly Func<AnnotationEditorState?> _getState;
    private readonly Func<Control?> _getContentControl;
    private readonly Func<Bitmap?> _getContentSnapshot;
    private readonly Func<bool> _isInteractionBlocked;
    private readonly Action _confirmTextEntry;
    private readonly Action _focusTextInput;
    private readonly IInputElement _captureTarget;
    private readonly Action<Cursor> _setCursor;

    // Drawing state
    private Annotation? _currentAnnotation;
    private bool _isDrawing;
    private DateTime _lastTextFinishTime = DateTime.MinValue;

    // Drag-annotation state
    private bool _isDraggingAnnotation;
    private Annotation? _draggingAnnotation;
    private Point _dragOffset;
    private AnnotationSnapshot? _annotationEditBefore;
    private AnnotationHitZone _annotationHitZone;
    private Point _annotationDragStart;

    public AnnotationInputController(
        Func<AnnotationEditorState?> getState,
        Func<Control?> getContentControl,
        Func<Bitmap?> getContentSnapshot,
        Func<bool> isInteractionBlocked,
        Action confirmTextEntry,
        Action focusTextInput,
        IInputElement captureTarget,
        Action<Cursor> setCursor)
    {
        _getState = getState;
        _getContentControl = getContentControl;
        _getContentSnapshot = getContentSnapshot;
        _isInteractionBlocked = isInteractionBlocked;
        _confirmTextEntry = confirmTextEntry;
        _focusTextInput = focusTextInput;
        _captureTarget = captureTarget;
        _setCursor = setCursor;
    }

    /// <summary>True while a shape/pen drag is in progress.</summary>
    public bool IsDrawing => _isDrawing;

    /// <summary>True while an existing annotation is being dragged/resized.</summary>
    public bool IsDraggingAnnotation => _isDraggingAnnotation;

    /// <summary>Call when text entry finishes to arm the 300&#160;ms re-entry guard (parity with the Snip window).</summary>
    public void NotifyTextEntryFinished() => _lastTextFinishTime = DateTime.Now;

    /// <summary>
    /// Handles the "annotation tool active" press branch. Returns true when the branch APPLIED (an
    /// annotation tool is active and interaction isn't blocked) — the host must then stop processing the
    /// press, exactly like the original early-returns did. Returns false when the branch doesn't apply
    /// and the host should continue with its own branches (selection / window move…).
    /// </summary>
    public bool HandlePointerPressed(PointerPressedEventArgs e)
    {
        AnnotationEditorState? state = _getState();
        if (state == null || state.CurrentAnnotationTool == AnnotationType.None || _isInteractionBlocked())
        {
            return false;
        }

        if (!e.GetCurrentPoint(_captureTarget as Visual).Properties.IsLeftButtonPressed)
        {
            return false;
        }

        var contentControl = _getContentControl();
        if (contentControl == null)
        {
            return true;
        }

        var pointerPosOnContent = e.GetPosition(contentControl);
        var contentBounds = new Rect(0, 0, contentControl.Bounds.Width, contentControl.Bounds.Height);
        var interactionBounds = state.SelectedAnnotation != null
            ? contentBounds.Inflate(AnnotationInteractionService.HandleRadius + 2)
            : contentBounds;

        // Restrict drawing interaction to the content area
        if (!interactionBounds.Contains(pointerPosOnContent))
        {
            return true;
        }

        if ((DateTime.Now - _lastTextFinishTime).TotalMilliseconds < 300)
        {
            return true;
        }

        if (state.IsEnteringText)
        {
            var src = e.Source as Control;
            if (src != null && (src.Name == "TextInputOverlay" || src.FindAncestorOfType<TextBox>() != null))
            {
                return true;
            }

            // Clicking outside text box confirms text
            _confirmTextEntry();
            e.Handled = true;
            return true;
        }

        var annotationHit = AnnotationInteractionService.HitTest(
            state.Annotations,
            state.SelectedAnnotation,
            pointerPosOnContent);
        if (annotationHit.IsHit && annotationHit.Annotation != null)
        {
            _isDraggingAnnotation = true;
            _draggingAnnotation = annotationHit.Annotation;
            _annotationHitZone = annotationHit.Zone;
            _annotationDragStart = pointerPosOnContent;
            _annotationEditBefore = state.BeginAnnotationEdit(annotationHit.Annotation);
            if (annotationHit.Annotation.Type == AnnotationType.Text)
            {
                _dragOffset = new Point(
                    pointerPosOnContent.X - annotationHit.Annotation.StartPoint.X,
                    pointerPosOnContent.Y - annotationHit.Annotation.StartPoint.Y);
            }
            e.Pointer.Capture(_captureTarget);
            e.Handled = true;
            return true;
        }

        if (!contentBounds.Contains(pointerPosOnContent))
        {
            return true;
        }

        state.ClearSelection();

        if (state.CurrentAnnotationTool == AnnotationType.Text)
        {
            // Check if clicking existing text to edit/drag
            for (int i = state.Annotations.Count - 1; i >= 0; i--)
            {
                var ann = state.Annotations[i];
                if (ann.Type == AnnotationType.Text)
                {
                    double estimatedWidth = ann.Text.Length * ann.FontSize * 0.6;
                    double estimatedHeight = ann.FontSize * 1.5;
                    var rect = new Rect(ann.StartPoint.X, ann.StartPoint.Y, estimatedWidth, estimatedHeight);
                    if (rect.Contains(pointerPosOnContent))
                    {
                        if (e.ClickCount == 2)
                        {
                            // Edit Mode
                            state.RemoveAnnotation(ann);
                            state.IsEnteringText = true;
                            state.TextInputPosition = ann.StartPoint;
                            state.PendingText = ann.Text;
                            state.CurrentFontSize = ann.FontSize;
                            state.IsBold = ann.IsBold;
                            state.IsItalic = ann.IsItalic;
                            state.SelectedColor = ann.Color;

                            Dispatcher.UIThread.Post(_focusTextInput);
                            e.Handled = true;
                            return true;
                        }
                        else
                        {
                            // Drag Mode
                            _isDraggingAnnotation = true;
                            _draggingAnnotation = ann;
                            _dragOffset = new Point(pointerPosOnContent.X - ann.StartPoint.X, pointerPosOnContent.Y - ann.StartPoint.Y);
                            _annotationHitZone = AnnotationHitZone.Body;
                            _annotationDragStart = pointerPosOnContent;
                            _annotationEditBefore = state.BeginAnnotationEdit(ann);
                            e.Pointer.Capture(_captureTarget);
                            e.Handled = true;
                            return true;
                        }
                    }
                }
            }

            // Start NEW Text Entry
            state.IsEnteringText = true;
            state.TextInputPosition = pointerPosOnContent;
            state.PendingText = string.Empty;
            _focusTextInput();
            e.Handled = true;
            return true;
        }

        // Start Drawing Shape/Pen
        _isDrawing = true;

        // Snapshot is only needed for effects that sample underlying pixels.
        Bitmap? frameSnapshot = (state.CurrentAnnotationTool == AnnotationType.Mosaic || state.CurrentAnnotationTool == AnnotationType.Blur)
            ? _getContentSnapshot()
            : null;

        _currentAnnotation = state.CreateAnnotationForCurrentTool(
            pointerPosOnContent,
            frameSnapshot,
            contentControl.Bounds.Size);

        state.BeginPendingAnnotation(_currentAnnotation);
        e.Pointer.Capture(_captureTarget);
        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Cursor update + the drawing/drag-transform move branches. Call every pointer-move (after the
    /// host's own resize/selection branches); no-ops when neither drawing nor dragging.
    /// </summary>
    public void HandlePointerMoved(PointerEventArgs e, bool suppressCursor = false)
    {
        AnnotationEditorState? state = _getState();
        if (state == null)
        {
            return;
        }

        if (!suppressCursor)
        {
            UpdateCursor(e, state);
        }

        if (_isDrawing && _currentAnnotation != null)
        {
            var contentControl = _getContentControl();
            if (contentControl != null)
            {
                var pointerPosOnContent = e.GetPosition(contentControl);
                pointerPosOnContent = new Point(
                    Math.Clamp(pointerPosOnContent.X, 0, contentControl.Bounds.Width),
                    Math.Clamp(pointerPosOnContent.Y, 0, contentControl.Bounds.Height));
                if (_currentAnnotation.Type == AnnotationType.Pen)
                {
                    _currentAnnotation.AddPoint(pointerPosOnContent);
                }
                else
                {
                    _currentAnnotation.EndPoint = pointerPosOnContent;
                }
                e.Handled = true;
            }
        }
        else if (_isDraggingAnnotation && _draggingAnnotation != null)
        {
            var contentControl = _getContentControl();
            if (contentControl != null)
            {
                var pointerPosOnContent = e.GetPosition(contentControl);
                if (_draggingAnnotation.Type == AnnotationType.Text)
                {
                    var estimatedWidth = _draggingAnnotation.Text.Length * _draggingAnnotation.FontSize * 0.6;
                    var estimatedHeight = _draggingAnnotation.FontSize * 1.5;
                    var newStart = new Point(
                        Math.Clamp(pointerPosOnContent.X - _dragOffset.X, 0, Math.Max(0, contentControl.Bounds.Width - estimatedWidth)),
                        Math.Clamp(pointerPosOnContent.Y - _dragOffset.Y, 0, Math.Max(0, contentControl.Bounds.Height - estimatedHeight)));
                    var delta = newStart - _draggingAnnotation.StartPoint;
                    _draggingAnnotation.StartPoint = newStart;
                    _draggingAnnotation.EndPoint = new Point(
                        _draggingAnnotation.EndPoint.X + delta.X,
                        _draggingAnnotation.EndPoint.Y + delta.Y);
                }
                else if (_annotationEditBefore != null)
                {
                    AnnotationInteractionService.ApplyTransform(
                        _draggingAnnotation,
                        _annotationEditBefore,
                        _annotationHitZone,
                        _annotationDragStart,
                        pointerPosOnContent,
                        contentControl.Bounds.Size);
                }
            }
        }
    }

    /// <summary>
    /// The drawing/drag-transform release branches. Returns true when it consumed the release
    /// (the host must not run its own release branches).
    /// </summary>
    public bool HandlePointerReleased(PointerReleasedEventArgs e)
    {
        if (_isDrawing)
        {
            e.Pointer.Capture(null);
            _isDrawing = false;
            AnnotationEditorState? state = _getState();
            if (state != null && _currentAnnotation != null)
            {
                if (_currentAnnotation.Type == AnnotationType.Callout)
                {
                    BeginCalloutLabelEntry(state, _currentAnnotation);
                }
                else
                {
                    state.CommitPendingAnnotation(_currentAnnotation);
                }
            }
            _currentAnnotation = null;
            return true;
        }

        if (_isDraggingAnnotation)
        {
            e.Pointer.Capture(null);
            AnnotationEditorState? state = _getState();
            if (state != null && _draggingAnnotation != null && _annotationEditBefore != null)
            {
                state.CommitAnnotationEdit(_draggingAnnotation, _annotationEditBefore);
            }
            _isDraggingAnnotation = false;
            _draggingAnnotation = null;
            _annotationEditBefore = null;
            _annotationHitZone = AnnotationHitZone.None;
            return true;
        }

        return false;
    }

    // After the Callout leader is dragged, open the label text-entry overlay at the leader's end
    // (label anchor). A click with no real drag gets a default offset so the leader stays visible
    // and valid. The pending leader is finalized by the host's confirm-text-entry command.
    private void BeginCalloutLabelEntry(AnnotationEditorState state, Annotation leader)
    {
        var dx = leader.EndPoint.X - leader.StartPoint.X;
        var dy = leader.EndPoint.Y - leader.StartPoint.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) < AnnotationInteractionService.MinimumLineLength)
        {
            leader.EndPoint = new Point(leader.StartPoint.X + 48, leader.StartPoint.Y + 48);
        }

        state.BeginCalloutTextEntry(leader);
        state.IsEnteringText = true;
        state.TextInputPosition = leader.EndPoint;
        state.PendingText = leader.Text ?? string.Empty;

        Dispatcher.UIThread.Post(_focusTextInput);
    }

    /// <summary>Annotation-aware cursor (cross/I-beam/resize/hand) while a tool is active.</summary>
    public void UpdateCursor(PointerEventArgs e, AnnotationEditorState? state = null, bool hostBusy = false)
    {
        state ??= _getState();
        if (state == null || state.CurrentAnnotationTool == AnnotationType.None || _isInteractionBlocked())
        {
            return;
        }

        if (_isDraggingAnnotation)
        {
            _setCursor(CreateAnnotationCursor(_annotationHitZone));
            return;
        }

        if (_isDrawing || hostBusy)
        {
            return;
        }

        var contentControl = _getContentControl();
        if (contentControl == null)
        {
            return;
        }

        var point = e.GetPosition(contentControl);
        var contentBounds = new Rect(0, 0, contentControl.Bounds.Width, contentControl.Bounds.Height);
        var interactionBounds = state.SelectedAnnotation != null
            ? contentBounds.Inflate(AnnotationInteractionService.HandleRadius + 2)
            : contentBounds;
        if (!interactionBounds.Contains(point))
        {
            _setCursor(new Cursor(StandardCursorType.Arrow));
            return;
        }

        var hit = AnnotationInteractionService.HitTest(state.Annotations, state.SelectedAnnotation, point);
        _setCursor(hit.IsHit
            ? CreateAnnotationCursor(hit.Zone)
            : new Cursor(contentBounds.Contains(point)
                ? (state.CurrentAnnotationTool == AnnotationType.Text
                    ? StandardCursorType.Ibeam
                    : StandardCursorType.Cross)
                : StandardCursorType.Arrow));
    }

    private static Cursor CreateAnnotationCursor(AnnotationHitZone zone)
    {
        var cursorType = zone switch
        {
            AnnotationHitZone.TopLeft or AnnotationHitZone.BottomRight => StandardCursorType.TopLeftCorner,
            AnnotationHitZone.TopRight or AnnotationHitZone.BottomLeft => StandardCursorType.TopRightCorner,
            AnnotationHitZone.Top or AnnotationHitZone.Bottom => StandardCursorType.SizeNorthSouth,
            AnnotationHitZone.Left or AnnotationHitZone.Right => StandardCursorType.SizeWestEast,
            AnnotationHitZone.StartPoint or AnnotationHitZone.EndPoint => StandardCursorType.Hand,
            _ => StandardCursorType.SizeAll
        };
        return new Cursor(cursorType);
    }
}
