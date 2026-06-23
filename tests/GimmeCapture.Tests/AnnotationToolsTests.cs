using System.Collections.Generic;
using System.Collections.ObjectModel;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Tests;

public class AnnotationToolsTests
{
    [Fact]
    public void BringToFront_MovesAnnotationLast_AndUndoRestoresOrder()
    {
        var a = new Annotation { Type = AnnotationType.Rectangle };
        var b = new Annotation { Type = AnnotationType.Rectangle };
        var c = new Annotation { Type = AnnotationType.Rectangle };
        var collection = new ObservableCollection<Annotation> { a, b, c };

        var action = new AnnotationReorderHistoryAction(collection, a, 0, collection.Count - 1);
        collection.Move(0, collection.Count - 1); // simulate BringToFront(a)

        Assert.Equal(new[] { b, c, a }, collection);

        action.Undo();
        Assert.Equal(new[] { a, b, c }, collection);

        action.Redo();
        Assert.Equal(new[] { b, c, a }, collection);
    }

    [Fact]
    public void SendToBack_MovesAnnotationFirst_AndUndoRestoresOrder()
    {
        var a = new Annotation { Type = AnnotationType.Rectangle };
        var b = new Annotation { Type = AnnotationType.Rectangle };
        var c = new Annotation { Type = AnnotationType.Rectangle };
        var collection = new ObservableCollection<Annotation> { a, b, c };

        var action = new AnnotationReorderHistoryAction(collection, c, 2, 0);
        collection.Move(2, 0); // simulate SendToBack(c)

        Assert.Equal(new[] { c, a, b }, collection);

        action.Undo();
        Assert.Equal(new[] { a, b, c }, collection);
    }

    [Fact]
    public void NextStepNumber_EmptyCollection_ReturnsOne()
    {
        Assert.Equal(1, AnnotationEditorState.NextStepNumber(new List<Annotation>()));
    }

    [Fact]
    public void NextStepNumber_IgnoresNonStepAnnotations()
    {
        var annotations = new List<Annotation>
        {
            new() { Type = AnnotationType.Rectangle },
            new() { Type = AnnotationType.Step, StepNumber = 1 },
            new() { Type = AnnotationType.Highlighter },
            new() { Type = AnnotationType.Step, StepNumber = 2 },
        };

        Assert.Equal(3, AnnotationEditorState.NextStepNumber(annotations));
    }

    [Fact]
    public void NextStepNumber_UsesMaxPlusOne_AfterDeletion()
    {
        // #2 was deleted, leaving only #1 -> next is max(1)+1 = 2.
        var annotations = new List<Annotation>
        {
            new() { Type = AnnotationType.Step, StepNumber = 1 },
        };

        Assert.Equal(2, AnnotationEditorState.NextStepNumber(annotations));
    }

    [Theory]
    [InlineData(AnnotationType.Rectangle, true)]
    [InlineData(AnnotationType.Ellipse, true)]
    [InlineData(AnnotationType.Highlighter, false)]
    [InlineData(AnnotationType.Step, false)]
    [InlineData(AnnotationType.Line, false)]
    public void SupportsFill_OnlyOutlinedShapes(AnnotationType type, bool expected)
    {
        Assert.Equal(expected, AnnotationEditorState.SupportsFill(type));
    }
}
