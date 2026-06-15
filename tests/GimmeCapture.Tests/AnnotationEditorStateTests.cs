using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Tests;

public class AnnotationEditorStateTests
{
    [Fact]
    public void ToggleToolGroup_RemembersLastRedactionToolAndPreset()
    {
        var state = new AnnotationEditorState();

        state.SelectTool(AnnotationType.Blur);
        state.SetRedactionPreset("Strong");
        state.ToggleToolGroup("Redaction");
        state.ToggleToolGroup("Redaction");

        Assert.Equal(AnnotationType.Blur, state.CurrentAnnotationTool);
        Assert.Equal("Strong", state.CurrentRedactionPreset);

        var effect = state.CreateEffectSettingsFor(AnnotationType.Blur);
        Assert.Equal(28f, effect.BlurRadius);
    }

    [Fact]
    public void UndoRedo_WorksForAddedAnnotations()
    {
        var state = new AnnotationEditorState();
        var annotation = new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(1, 1),
            EndPoint = new Point(4, 4)
        };

        state.AddAnnotation(annotation);
        Assert.Single(state.Annotations);

        state.Undo();
        Assert.Empty(state.Annotations);

        state.Redo();
        Assert.Single(state.Annotations);
    }

    [Fact]
    public void CreateAnnotationForCurrentTool_CopiesCurrentStyleAndEffectSettings()
    {
        var state = new AnnotationEditorState
        {
            SelectedColor = Colors.Cyan,
            CurrentThickness = 6,
            CurrentFontSize = 32,
            IsBold = true,
            IsItalic = true
        };

        state.SelectTool(AnnotationType.Blur);
        state.SetRedactionPreset("Strong");

        var annotation = state.CreateAnnotationForCurrentTool(
            new Point(3, 4),
            drawingModeReferenceSize: new Size(100, 80));

        Assert.Equal(AnnotationType.Blur, annotation.Type);
        Assert.Equal(new Point(3, 4), annotation.StartPoint);
        Assert.Equal(new Point(3, 4), annotation.EndPoint);
        Assert.Equal(Colors.Cyan, annotation.Color);
        Assert.Equal(6, annotation.Thickness);
        Assert.Equal(32, annotation.FontSize);
        Assert.True(annotation.IsBold);
        Assert.True(annotation.IsItalic);
        Assert.Equal(new Size(100, 80), annotation.DrawingModeReferenceSize);
        Assert.Equal(28f, annotation.EffectSettings.BlurRadius);
    }

    [Fact]
    public void PendingAnnotation_IsCommittedOnceAndSelected()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Rectangle);
        var annotation = state.CreateAnnotationForCurrentTool(new Point(10, 10));
        annotation.EndPoint = new Point(40, 50);

        state.BeginPendingAnnotation(annotation);
        Assert.False(state.HasUndo);

        Assert.True(state.CommitPendingAnnotation(annotation));
        Assert.True(state.HasUndo);
        Assert.Same(annotation, state.SelectedAnnotation);
        Assert.True(annotation.IsSelected);

        state.Undo();
        Assert.Empty(state.Annotations);
        Assert.Null(state.SelectedAnnotation);
    }

    [Fact]
    public void PendingAnnotation_RejectsTinyGeometryWithoutHistory()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Rectangle);
        var annotation = state.CreateAnnotationForCurrentTool(new Point(10, 10));
        annotation.EndPoint = new Point(14, 14);

        state.BeginPendingAnnotation(annotation);

        Assert.False(state.CommitPendingAnnotation(annotation));
        Assert.Empty(state.Annotations);
        Assert.False(state.HasUndo);
    }

    [Fact]
    public void AnnotationEdit_IsOneUndoStep()
    {
        var state = new AnnotationEditorState();
        var annotation = new Annotation
        {
            Type = AnnotationType.Arrow,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40),
            Thickness = 2
        };
        state.AddAnnotation(annotation);
        var before = state.BeginAnnotationEdit(annotation);
        annotation.EndPoint = new Point(80, 60);
        state.CommitAnnotationEdit(annotation, before);

        state.Undo();
        Assert.Equal(new Point(40, 40), annotation.EndPoint);

        state.Redo();
        Assert.Equal(new Point(80, 60), annotation.EndPoint);
    }

    [Fact]
    public void RemoveSelectedAnnotation_RemovesOnlySelectionAndSupportsUndo()
    {
        var state = new AnnotationEditorState();
        var first = new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40)
        };
        var selected = new Annotation
        {
            Type = AnnotationType.Ellipse,
            StartPoint = new Point(50, 50),
            EndPoint = new Point(90, 90)
        };
        state.AddAnnotation(first);
        state.AddAnnotation(selected);

        Assert.True(state.RemoveSelectedAnnotation());
        Assert.Single(state.Annotations);
        Assert.Same(first, state.Annotations[0]);
        Assert.Null(state.SelectedAnnotation);

        state.Undo();
        Assert.Equal(2, state.Annotations.Count);
        Assert.Contains(selected, state.Annotations);
    }

    [Fact]
    public void SelectedThicknessChange_IsUndoable()
    {
        var state = new AnnotationEditorState();
        var annotation = new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40),
            Thickness = 2
        };
        state.AddAnnotation(annotation);

        state.ApplySelectedThickness(7);
        Assert.Equal(7, annotation.Thickness);

        state.Undo();
        Assert.Equal(2, annotation.Thickness);
    }

    [Fact]
    public void ToolChange_ClearsVisualSelectionWithoutRemovingAnnotation()
    {
        var state = new AnnotationEditorState();
        var annotation = new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40)
        };
        state.AddAnnotation(annotation);

        state.SelectTool(AnnotationType.Arrow);

        Assert.Null(state.SelectedAnnotation);
        Assert.False(annotation.IsSelected);
        Assert.Single(state.Annotations);
    }
}
