using GimmeCapture.Models;
using GimmeCapture.Services.Core.Rendering;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class RedactionRendererTests
{
    [Fact]
    public void ToPixelRect_MapsNormalizedToPixels()
    {
        var rect = RedactionRenderer.ToPixelRect(new RedactionBox(0.25, 0.5, 0.5, 0.25), 800, 600);

        Assert.Equal(200d, rect.Left, 3);
        Assert.Equal(300d, rect.Top, 3);
        Assert.Equal(600d, rect.Right, 3);
        Assert.Equal(450d, rect.Bottom, 3);
    }

    [Fact]
    public void ToPixelRect_ClampsToFrameBounds()
    {
        // Box starts before 0 and runs past 1.0 — must clamp to [0, frame].
        var rect = RedactionRenderer.ToPixelRect(new RedactionBox(-0.5, 0.5, 2.0, 2.0), 100, 100);

        Assert.Equal(0d, rect.Left, 3);
        Assert.Equal(50d, rect.Top, 3);
        Assert.Equal(100d, rect.Right, 3);
        Assert.Equal(100d, rect.Bottom, 3);
    }

    [Fact]
    public void Render_SolidBlack_BlackensBoxAndLeavesOutsideUntouched()
    {
        using var frame = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.White);
        }

        var track = new RedactionTrack { Effect = RedactionEffect.SolidBlack };
        track.Keyframes.Add(new RedactionKeyframe { TimeSeconds = 0, X = 0.4, Y = 0.4, Width = 0.2, Height = 0.2 });

        RedactionRenderer.Render(frame, new[] { track }, 0.0);

        Assert.Equal(SKColors.Black, frame.GetPixel(50, 50)); // inside the box
        Assert.Equal(SKColors.White, frame.GetPixel(5, 5));   // far outside
    }

    [Fact]
    public void Render_MovesBoxOverTime()
    {
        using var frame = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.White);
        }

        var track = new RedactionTrack { Effect = RedactionEffect.SolidBlack };
        track.Keyframes.Add(new RedactionKeyframe { TimeSeconds = 0, X = 0.0, Y = 0.0, Width = 0.2, Height = 0.2 });
        track.Keyframes.Add(new RedactionKeyframe { TimeSeconds = 2, X = 0.8, Y = 0.8, Width = 0.2, Height = 0.2 });

        // At t=2 the box has moved to the bottom-right corner.
        RedactionRenderer.Render(frame, new[] { track }, 2.0);

        Assert.Equal(SKColors.Black, frame.GetPixel(90, 90)); // box now here
        Assert.Equal(SKColors.White, frame.GetPixel(5, 5));   // was here at t=0, now clear
    }

    [Fact]
    public void Render_NoTracks_IsNoOp()
    {
        using var frame = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.White);
        }

        RedactionRenderer.Render(frame, null, 0.0);
        RedactionRenderer.Render(frame, new RedactionTrack[0], 0.0);

        Assert.Equal(SKColors.White, frame.GetPixel(5, 5));
    }
}
