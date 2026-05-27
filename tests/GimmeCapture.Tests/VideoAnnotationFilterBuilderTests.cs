using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;

namespace GimmeCapture.Tests;

public class VideoAnnotationFilterBuilderTests
{
    [Fact]
    public void BuildFilter_BlurAnnotation_UsesBoxBlurAndOverlayChain()
    {
        var annotation = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Point(10, 20),
            EndPoint = new Point(110, 70)
        };
        annotation.EffectSettings.BlurRadius = 10f;

        string filter = VideoAnnotationFilterBuilder.BuildFilter(
            new[] { annotation },
            referenceWidth: 200,
            referenceHeight: 100,
            targetWidth: 400,
            targetHeight: 200,
            includeOverlayInput: true,
            isOutputGif: false);

        Assert.Contains("boxblur=", filter);
        Assert.Contains("luma_power=2", filter);
        Assert.DoesNotContain("eq=contrast", filter);
        Assert.Contains("crop=w=280:h=200:x=0:y=0", filter);
        Assert.Contains("crop=w=200:h=100:x=20:y=40", filter);
        Assert.Contains("[1:v][v1]scale2ref", filter);
        Assert.Contains("[vcomposite]null[outv]", filter);
    }

    [Fact]
    public void BuildFilter_MosaicAnnotation_UsesPixelationScaleChain()
    {
        var annotation = new Annotation
        {
            Type = AnnotationType.Mosaic,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(120, 60)
        };
        annotation.EffectSettings.MosaicCellSize = 12;

        string filter = VideoAnnotationFilterBuilder.BuildFilter(
            new[] { annotation },
            referenceWidth: 240,
            referenceHeight: 120,
            targetWidth: 240,
            targetHeight: 120,
            includeOverlayInput: true,
            isOutputGif: true);

        Assert.Contains("scale=10:5:flags=neighbor,scale=120:60:flags=neighbor", filter);
        Assert.Contains("palettegen", filter);
        Assert.Contains("paletteuse[outv]", filter);
    }
}
