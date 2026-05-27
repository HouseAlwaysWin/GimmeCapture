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
}
