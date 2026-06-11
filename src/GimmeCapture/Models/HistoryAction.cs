using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using System.Runtime.CompilerServices;

namespace GimmeCapture.Models;

public interface IHistoryAction : IDisposable
{
    void Undo();
    void Redo();
}

public class BitmapHistoryAction : IHistoryAction
{
    private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static readonly object _bitmapRefLock = new();
    private static readonly Dictionary<Bitmap, int> _bitmapRefCounts = new(new ReferenceEqualityComparer<Bitmap>());

    public Action<Bitmap?> SetBitmapAction { get; }
    public Bitmap? OldBitmap { get; }
    public Bitmap? NewBitmap { get; }
    private readonly bool _trackOldBitmap;
    private readonly bool _trackNewBitmap;
    private readonly Func<Bitmap?>? _getCurrentBitmap;
    private bool _disposed;

    public BitmapHistoryAction(
        Action<Bitmap?> setBitmap,
        Bitmap? oldBitmap,
        Bitmap? newBitmap,
        bool trackOldBitmap = true,
        bool trackNewBitmap = true,
        Func<Bitmap?>? getCurrentBitmap = null)
    {
        SetBitmapAction = setBitmap;
        OldBitmap = oldBitmap;
        NewBitmap = newBitmap;
        _trackOldBitmap = trackOldBitmap;
        _trackNewBitmap = trackNewBitmap;
        _getCurrentBitmap = getCurrentBitmap;

        AddBitmapRef(OldBitmap, _trackOldBitmap);
        AddBitmapRef(NewBitmap, _trackNewBitmap);
    }

    public void Undo() => SetBitmapAction(OldBitmap);
    public void Redo() => SetBitmapAction(NewBitmap);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseBitmapRef(OldBitmap, _trackOldBitmap);
        ReleaseBitmapRef(NewBitmap, _trackNewBitmap);
    }

    private static void AddBitmapRef(Bitmap? bitmap, bool shouldTrack)
    {
        if (!shouldTrack || bitmap == null) return;
        lock (_bitmapRefLock)
        {
            _bitmapRefCounts.TryGetValue(bitmap, out var count);
            _bitmapRefCounts[bitmap] = count + 1;
        }
    }

    private void ReleaseBitmapRef(Bitmap? bitmap, bool shouldTrack)
    {
        if (!shouldTrack || bitmap == null) return;

        bool shouldDispose = false;
        lock (_bitmapRefLock)
        {
            if (!_bitmapRefCounts.TryGetValue(bitmap, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _bitmapRefCounts.Remove(bitmap);
                shouldDispose = true;
            }
            else
            {
                _bitmapRefCounts[bitmap] = count - 1;
            }
        }

        if (!shouldDispose) return;

        // Never dispose the currently displayed bitmap.
        if (ReferenceEquals(bitmap, _getCurrentBitmap?.Invoke())) return;

        try
        {
            bitmap.Dispose();
        }
        catch
        {
            // Ignore cleanup failures in history disposal path.
        }
    }
}

public class AnnotationHistoryAction : IHistoryAction
{
    private readonly ObservableCollection<Annotation> _annotations;
    private readonly Annotation _annotation;
    private readonly bool _isAdd;

    public AnnotationHistoryAction(ObservableCollection<Annotation> annotations, Annotation annotation, bool isAdd)
    {
        _annotations = annotations;
        _annotation = annotation;
        _isAdd = isAdd;
    }

    public void Undo()
    {
        if (_isAdd) _annotations.Remove(_annotation);
        else _annotations.Add(_annotation);
    }

    public void Redo()
    {
        if (_isAdd) _annotations.Add(_annotation);
        else _annotations.Remove(_annotation);
    }

    public void Dispose() { }
}

public sealed class AnnotationEditHistoryAction : IHistoryAction
{
    private readonly Annotation _annotation;
    private readonly AnnotationSnapshot _before;
    private readonly AnnotationSnapshot _after;

    public AnnotationEditHistoryAction(
        Annotation annotation,
        AnnotationSnapshot before,
        AnnotationSnapshot after)
    {
        _annotation = annotation;
        _before = before;
        _after = after;
    }

    public void Undo() => _before.ApplyTo(_annotation);
    public void Redo() => _after.ApplyTo(_annotation);
    public void Dispose() { }
}

public class ClearAnnotationsHistoryAction : IHistoryAction
{
    private readonly ObservableCollection<Annotation> _annotations;
    private readonly List<Annotation> _removedAnnotations;

    public ClearAnnotationsHistoryAction(ObservableCollection<Annotation> annotations)
    {
        _annotations = annotations;
        _removedAnnotations = new List<Annotation>(annotations);
    }

    public void Undo()
    {
        foreach (var a in _removedAnnotations) _annotations.Add(a);
    }

    public void Redo()
    {
        _annotations.Clear();
    }

    public void Dispose() { }
}

public class WindowTransformHistoryAction : IHistoryAction
{
    private readonly Action<Avalonia.PixelPoint, double, double, double, double> _setter;
    private readonly Avalonia.PixelPoint _oldPos;
    private readonly double _oldWidth;
    private readonly double _oldHeight;
    private readonly double _oldContentWidth;
    private readonly double _oldContentHeight;

    private readonly Avalonia.PixelPoint _newPos;
    private readonly double _newWidth;
    private readonly double _newHeight;
    private readonly double _newContentWidth;
    private readonly double _newContentHeight;

    public WindowTransformHistoryAction(Action<Avalonia.PixelPoint, double, double, double, double> setter, 
        Avalonia.PixelPoint oldPos, double oldWidth, double oldHeight, double oldContentWidth, double oldContentHeight,
        Avalonia.PixelPoint newPos, double newWidth, double newHeight, double newContentWidth, double newContentHeight)
    {
        _setter = setter;
        _oldPos = oldPos;
        _oldWidth = oldWidth;
        _oldHeight = oldHeight;
        _oldContentWidth = oldContentWidth;
        _oldContentHeight = oldContentHeight;
        _newPos = newPos;
        _newWidth = newWidth;
        _newHeight = newHeight;
        _newContentWidth = newContentWidth;
        _newContentHeight = newContentHeight;
    }

    public void Undo() => _setter(_oldPos, _oldWidth, _oldHeight, _oldContentWidth, _oldContentHeight);
    public void Redo() => _setter(_newPos, _newWidth, _newHeight, _newContentWidth, _newContentHeight);

    public void Dispose() { }
}


public class CompositeHistoryAction : IHistoryAction
{
    private readonly List<IHistoryAction> _actions;

    public CompositeHistoryAction(IEnumerable<IHistoryAction> actions)
    {
        _actions = actions.AsValueEnumerable().ToList();
    }

    public void Undo()
    {
        // Undo in reverse order
        for (int i = _actions.Count - 1; i >= 0; i--)
        {
            _actions[i].Undo();
        }
    }

    public void Redo()
    {
        // Redo in order
        for (int i = 0; i < _actions.Count; i++)
        {
            _actions[i].Redo();
        }
    }

    public void Dispose()
    {
        foreach (var action in _actions)
        {
            action.Dispose();
        }
        _actions.Clear();
    }
}
