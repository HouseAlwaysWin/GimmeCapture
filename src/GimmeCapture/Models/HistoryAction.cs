using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;

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
