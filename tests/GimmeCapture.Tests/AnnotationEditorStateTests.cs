using Avalonia;
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
        Assert.Equal(60f, effect.BlurRadius);
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
}
