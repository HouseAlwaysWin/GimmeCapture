using Avalonia;
using GimmeCapture.Views.Controls;

namespace GimmeCapture.Tests;

/// <summary>
/// The size a mosaic or blur preview is drawn in. A pin's annotation controls take the pin's size, so a preview that
/// followed the control's size was redrawn on every frame of a resize: 180 redraws for six redactions over 30 frames
/// (RedactionPreviewMemoryTests).
/// </summary>
public class AnnotationControlTests
{
    [Fact]
    public void APreview_IsDrawnInTheSizeItsAnnotationWasDrawnIn_WhateverTheControlsSize()
    {
        var drawnIn = new Size(800, 600);

        Assert.Equal(drawnIn, AnnotationControl.PreviewReferenceSize(drawnIn, new Size(400, 300), new PixelSize(1600, 1200)));
        Assert.Equal(drawnIn, AnnotationControl.PreviewReferenceSize(drawnIn, new Size(1600, 1200), default));
    }

    [Fact]
    public void AnAnnotationWithNoSizeOfItsOwn_IsDrawnInTheControlsSize_ElseThePictures()
    {
        var picture = new PixelSize(320, 200);

        Assert.Equal(new Size(400, 300), AnnotationControl.PreviewReferenceSize(default, new Size(400, 300), picture));
        Assert.Equal(new Size(320, 200), AnnotationControl.PreviewReferenceSize(default, default, picture));
        Assert.Equal(
            new Size(320, 300),
            AnnotationControl.PreviewReferenceSize(new Size(double.NaN, 0), new Size(0, 300), picture));
    }
}
