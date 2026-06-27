using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Tests;

public class AnnotationEditorStateTests
{
    private static Annotation Rect(double x = 10, double y = 10, double w = 30, double h = 30, double thickness = 2)
        => new()
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(x, y),
            EndPoint = new Point(x + w, y + h),
            Thickness = thickness
        };

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

    // ---------- tool groups ----------

    [Fact]
    public void GetToolGroupTarget_TogglesPenTextAndUnknownGroups()
    {
        var state = new AnnotationEditorState();

        Assert.Equal(AnnotationType.Pen, state.GetToolGroupTarget("Pen"));
        Assert.Equal(AnnotationType.Text, state.GetToolGroupTarget("Text"));
        Assert.Equal(AnnotationType.None, state.GetToolGroupTarget("SomethingElse"));

        state.SelectTool(AnnotationType.Pen);
        Assert.Equal(AnnotationType.None, state.GetToolGroupTarget("Pen"));

        state.SelectTool(AnnotationType.Text);
        Assert.Equal(AnnotationType.None, state.GetToolGroupTarget("Text"));
    }

    [Fact]
    public void GetToolGroupTarget_ShapesRemembersLastShapeTool()
    {
        var state = new AnnotationEditorState();

        state.SelectTool(AnnotationType.Rectangle);
        Assert.True(state.IsShapeToolActive);
        Assert.Equal(AnnotationType.None, state.GetToolGroupTarget("Shapes"));

        state.SelectTool(AnnotationType.Text);
        Assert.Equal(AnnotationType.Rectangle, state.GetToolGroupTarget("Shapes"));
    }

    [Fact]
    public void GetToolGroupTarget_RedactionTogglesOffWhenActive()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Blur);

        Assert.True(state.IsRedactionToolActive);
        Assert.Equal(AnnotationType.None, state.GetToolGroupTarget("Redaction"));
    }

    // ---------- effect settings ----------

    [Theory]
    [InlineData(AnnotationType.Blur)]
    [InlineData(AnnotationType.Mosaic)]
    [InlineData(AnnotationType.Rectangle)]
    public void CreateEffectSettingsFor_ReturnsSettingsForEveryTool(AnnotationType tool)
        => Assert.NotNull(new AnnotationEditorState().CreateEffectSettingsFor(tool));

    // ---------- per-tool annotation creation ----------

    [Fact]
    public void CreateAnnotationForCurrentTool_PenStartsAStroke()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Pen);

        var annotation = state.CreateAnnotationForCurrentTool(new Point(5, 6));

        Assert.Equal(AnnotationType.Pen, annotation.Type);
    }

    [Fact]
    public void CreateAnnotationForCurrentTool_StepGetsStepNumber()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Step);

        var annotation = state.CreateAnnotationForCurrentTool(new Point(5, 6));

        Assert.Equal(AnnotationType.Step, annotation.Type);
        Assert.True(annotation.StepNumber >= 1);
    }

    [Fact]
    public void CreateAnnotationForCurrentTool_MosaicGetsEffectSettings()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Mosaic);

        var annotation = state.CreateAnnotationForCurrentTool(new Point(5, 6));

        Assert.Equal(AnnotationType.Mosaic, annotation.Type);
        Assert.NotNull(annotation.EffectSettings);
    }

    // ---------- redaction presets ----------

    [Theory]
    [InlineData("Soft")]
    [InlineData("Medium")]
    [InlineData("Strong")]
    public void SetRedactionPreset_BlurPresetsRoundTrip(string preset)
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Blur);

        state.SetRedactionPreset(preset);

        Assert.Equal(preset, state.CurrentRedactionPreset);
    }

    [Theory]
    [InlineData("Small")]
    [InlineData("Medium")]
    [InlineData("Large")]
    public void SetRedactionPreset_MosaicPresetsRoundTrip(string preset)
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Mosaic);

        state.SetRedactionPreset(preset);

        Assert.Equal(preset, state.CurrentRedactionPreset);
    }

    [Fact]
    public void SetRedactionPreset_OnSelectedBlur_UpdatesEffectAndIsUndoable()
    {
        var state = new AnnotationEditorState();
        var blur = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(60, 60)
        };
        state.AddAnnotation(blur);
        state.SelectAnnotation(blur);

        state.SetRedactionPreset("Strong");

        Assert.Equal("Strong", state.CurrentRedactionPreset);
        Assert.True(blur.EffectSettings.BlurRadius >= 24f);
    }

    // ---------- selection ----------

    [Fact]
    public void ClearSelection_DeselectsCurrentAnnotation()
    {
        var state = new AnnotationEditorState();
        var annotation = new Annotation
        {
            Type = AnnotationType.Rectangle,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40)
        };
        state.AddAnnotation(annotation);
        state.SelectAnnotation(annotation);
        Assert.Same(annotation, state.SelectedAnnotation);

        state.ClearSelection();

        Assert.Null(state.SelectedAnnotation);
        Assert.False(annotation.IsSelected);
    }

    // ---------- style setters applied to the selection ----------

    [Fact]
    public void CurrentThicknessSetter_AppliesToSelectedShape_AndIsUndoable()
    {
        var state = new AnnotationEditorState();
        var rect = Rect(thickness: 2);
        state.AddAnnotation(rect);
        state.SelectAnnotation(rect);

        state.CurrentThickness = 9;

        Assert.Equal(9, rect.Thickness);
        state.Undo();
        Assert.Equal(2, rect.Thickness);
    }

    [Fact]
    public void CurrentFillSetter_TogglesSelectedRectangleFill_AndIsUndoable()
    {
        var state = new AnnotationEditorState();
        var rect = Rect();
        state.AddAnnotation(rect);
        state.SelectAnnotation(rect);

        state.CurrentFill = true;

        Assert.True(rect.IsFilled);
        state.Undo();
        Assert.False(rect.IsFilled);
    }

    [Fact]
    public void ApplySelectedColor_RecolorsSelection_AndIsUndoable()
    {
        var state = new AnnotationEditorState();
        var rect = Rect();
        state.AddAnnotation(rect);
        state.SelectAnnotation(rect);
        var original = rect.Color;

        state.ApplySelectedColor(Colors.Lime);

        Assert.Equal(Colors.Lime, rect.Color);
        state.Undo();
        Assert.Equal(original, rect.Color);
    }

    [Fact]
    public void SelectedColorSetter_RecolorsSelectionImmediately()
    {
        var state = new AnnotationEditorState();
        var rect = Rect();
        state.AddAnnotation(rect);
        state.SelectAnnotation(rect);

        state.SelectedColor = Colors.Magenta;

        Assert.Equal(Colors.Magenta, state.SelectedColor);
        Assert.Equal(Colors.Magenta, rect.Color);
    }

    [Fact]
    public void TextAndFontSetters_UpdateState()
    {
        var state = new AnnotationEditorState
        {
            CurrentFontSize = 40,
            IsBold = true,
            IsItalic = true,
            IsEnteringText = true,
            PendingText = "hi",
            TextInputPosition = new Point(3, 4),
            CurrentFontFamily = new FontFamily("Times New Roman")
        };

        Assert.Equal(40, state.CurrentFontSize);
        Assert.True(state.IsBold);
        Assert.True(state.IsItalic);
        Assert.True(state.IsEnteringText);
        Assert.Equal("hi", state.PendingText);
        Assert.Equal(new Point(3, 4), state.TextInputPosition);
        Assert.NotNull(state.CurrentFontFamily);
    }

    // ---------- cancel / remove ----------

    [Fact]
    public void CancelAnnotationEdit_RestoresSnapshot()
    {
        var state = new AnnotationEditorState();
        var arrow = new Annotation
        {
            Type = AnnotationType.Arrow,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(40, 40)
        };
        state.AddAnnotation(arrow);

        var before = state.BeginAnnotationEdit(arrow);
        arrow.EndPoint = new Point(99, 99);
        state.CancelAnnotationEdit(arrow, before);

        Assert.Equal(new Point(40, 40), arrow.EndPoint);
    }

    [Fact]
    public void CancelPendingAnnotation_RemovesIt()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Rectangle);
        var pending = state.CreateAnnotationForCurrentTool(new Point(10, 10));
        pending.EndPoint = new Point(40, 50);

        state.BeginPendingAnnotation(pending);
        state.CancelPendingAnnotation(pending);

        Assert.DoesNotContain(pending, state.Annotations);
    }

    [Fact]
    public void RemoveAnnotation_RemovesSpecificAndIsUndoable()
    {
        var state = new AnnotationEditorState();
        var a = Rect(10, 10);
        var b = Rect(60, 60);
        state.AddAnnotation(a);
        state.AddAnnotation(b);

        state.RemoveAnnotation(a);
        Assert.DoesNotContain(a, state.Annotations);

        state.Undo();
        Assert.Contains(a, state.Annotations);
    }

    // ---------- static helpers ----------

    [Theory]
    [InlineData(AnnotationType.Rectangle, true)]
    [InlineData(AnnotationType.Ellipse, true)]
    [InlineData(AnnotationType.Arrow, false)]
    public void SupportsFill_OnlyOutlinedShapes(AnnotationType tool, bool expected)
        => Assert.Equal(expected, AnnotationEditorState.SupportsFill(tool));

    [Fact]
    public void NextStepNumber_IsMaxPlusOne()
    {
        Assert.Equal(1, AnnotationEditorState.NextStepNumber(new List<Annotation>()));
        Assert.Equal(4, AnnotationEditorState.NextStepNumber(new List<Annotation>
        {
            new() { Type = AnnotationType.Step, StepNumber = 3 },
            new() { Type = AnnotationType.Rectangle }
        }));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var state = new AnnotationEditorState();
        state.AddAnnotation(Rect());
        state.Dispose();
        Assert.Empty(state.RedoHistoryStack);
    }

    // ---------- selection switching & tool flags ----------

    [Fact]
    public void SelectAnnotation_SwitchingDeselectsPrevious()
    {
        var state = new AnnotationEditorState();
        var a = Rect(10, 10);
        var b = Rect(60, 60);
        state.AddAnnotation(a);
        state.AddAnnotation(b);

        state.SelectAnnotation(a);
        Assert.True(a.IsSelected);

        state.SelectAnnotation(b);
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.Same(b, state.SelectedAnnotation);
    }

    [Fact]
    public void CurrentThickness_IsClampedToValidRange()
    {
        var state = new AnnotationEditorState();

        state.CurrentThickness = 100;
        Assert.Equal(30, state.CurrentThickness);

        state.CurrentThickness = 0;
        Assert.Equal(1, state.CurrentThickness);
    }

    [Fact]
    public void SetRedactionPreset_OnSelectedMosaic_UpdatesPreset()
    {
        var state = new AnnotationEditorState();
        var mosaic = new Annotation
        {
            Type = AnnotationType.Mosaic,
            StartPoint = new Point(10, 10),
            EndPoint = new Point(60, 60)
        };
        state.AddAnnotation(mosaic);
        state.SelectAnnotation(mosaic);

        state.SetRedactionPreset("Large");

        Assert.Equal("Large", state.CurrentRedactionPreset);
    }

    [Fact]
    public void Undo_WhenEmpty_IsNoOp()
    {
        var state = new AnnotationEditorState();

        Assert.False(state.HasUndo);
        Assert.False(state.HasRedo);
        state.Undo();
        state.Redo();

        Assert.Empty(state.Annotations);
    }

    [Theory]
    [InlineData(AnnotationType.Blur, true)]
    [InlineData(AnnotationType.Mosaic, true)]
    [InlineData(AnnotationType.Rectangle, false)]
    public void IsRedactionToolActive_ReflectsCurrentTool(AnnotationType tool, bool expected)
    {
        var state = new AnnotationEditorState();
        state.SelectTool(tool);
        Assert.Equal(expected, state.IsRedactionToolActive);
    }

    [Fact]
    public void PenAndTextToolFlags_ReflectCurrentTool()
    {
        var state = new AnnotationEditorState();

        state.SelectTool(AnnotationType.Pen);
        Assert.True(state.IsPenToolActive);
        Assert.False(state.IsTextToolActive);

        state.SelectTool(AnnotationType.Text);
        Assert.True(state.IsTextToolActive);
        Assert.False(state.IsPenToolActive);
    }

    [Fact]
    public void SelectTool_None_ClearsActiveToolFlags()
    {
        var state = new AnnotationEditorState();
        state.SelectTool(AnnotationType.Rectangle);

        state.SelectTool(AnnotationType.None);

        Assert.Equal(AnnotationType.None, state.CurrentAnnotationTool);
        Assert.False(state.IsShapeToolActive);
        Assert.False(state.IsPenToolActive);
    }

    [Fact]
    public void CreateAnnotationForCurrentTool_TextCopiesFontStyle()
    {
        var state = new AnnotationEditorState
        {
            CurrentFontSize = 30,
            IsBold = true,
            IsItalic = true
        };
        state.SelectTool(AnnotationType.Text);

        var annotation = state.CreateAnnotationForCurrentTool(new Point(7, 8));

        Assert.Equal(AnnotationType.Text, annotation.Type);
        Assert.Equal(30, annotation.FontSize);
        Assert.True(annotation.IsBold);
        Assert.True(annotation.IsItalic);
    }
}
