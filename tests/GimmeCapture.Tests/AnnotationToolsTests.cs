using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Tests;

public class AnnotationToolsTests
{
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
