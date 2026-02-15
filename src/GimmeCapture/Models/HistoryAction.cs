using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using System.Linq;

namespace GimmeCapture.Models;

public interface IHistoryAction
{
    void Undo();
    void Redo();
}

public class BitmapHistoryAction : IHistoryAction
{
    public Action<Bitmap?> SetBitmapAction { get; }
    public Bitmap? OldBitmap { get; }
    public Bitmap? NewBitmap { get; }

    public BitmapHistoryAction(Action<Bitmap?> setBitmap, Bitmap? oldBitmap, Bitmap? newBitmap)
    {
        SetBitmapAction = setBitmap;
        OldBitmap = oldBitmap;
        NewBitmap = newBitmap;
    }

    public void Undo() => SetBitmapAction(OldBitmap);
    public void Redo() => SetBitmapAction(NewBitmap);
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
}


public class CompositeHistoryAction : IHistoryAction
{
    private readonly List<IHistoryAction> _actions;

    public CompositeHistoryAction(IEnumerable<IHistoryAction> actions)
    {
        _actions = actions.ToList();
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
}
