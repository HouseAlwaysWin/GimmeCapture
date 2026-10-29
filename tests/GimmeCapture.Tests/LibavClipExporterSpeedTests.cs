using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// How a kept range's speed retimes its frames through <see cref="LibavClipExporter.OutputFrameClock"/>: each frame is
/// placed on the output timeline at its own time divided by the speed, and the clock decides how many output frames it
/// fills. Faster ranges drop frames and slow motion repeats them.
/// </summary>
public class LibavClipExporterSpeedTests
{
    // Plays sourceFrames frames of a 30 fps clip at the given speed through a 30 fps clock and counts the output frames.
    private static long TotalEmitted(double speed, int sourceFrames)
    {
        var clock = new LibavClipExporter.OutputFrameClock(30);
        long total = 0;
        for (int i = 0; i < sourceFrames; i++)
        {
            total += clock.Arrive(i / 30.0 / speed, out _);
        }

        total += clock.Release(sourceFrames / 30.0 / speed, out _);
        Assert.Equal(total, clock.Emitted);
        return total;
    }

    [Fact]
    public void NormalSpeed_EmitsExactlyOnePerFrame()
    {
        var clock = new LibavClipExporter.OutputFrameClock(30);
        Assert.Equal(0, clock.Arrive(0, out _)); // held until the next frame says how long it shows
        for (int i = 1; i < 100; i++)
        {
            Assert.Equal(1, clock.Arrive(i / 30.0, out long firstPts));
            Assert.Equal(i - 1, firstPts);
        }

        Assert.Equal(1, clock.Release(100 / 30.0, out long lastPts));
        Assert.Equal(99, lastPts);
    }

    [Fact]
    public void DoubleSpeed_HalvesTheFrameCount()
    {
        // 2× → ~half the source frames survive.
        Assert.Equal(50, TotalEmitted(2.0, 100));
    }

    [Fact]
    public void HalfSpeed_DoublesTheFrameCount()
    {
        // 0.5× → each frame duplicated → ~twice the frames.
        Assert.Equal(200, TotalEmitted(0.5, 100));
    }

    [Fact]
    public void OnePointFiveSpeed_KeepsTwoThirds()
    {
        // 1.5× over 90 source frames → 60 output frames.
        Assert.Equal(60, TotalEmitted(1.5, 90));
    }

    [Fact]
    public void DoubleSpeed_KeepsEveryOtherFrame_Evenly()
    {
        var clock = new LibavClipExporter.OutputFrameClock(30);
        var kept = new System.Collections.Generic.List<int>();
        clock.Arrive(0, out _);
        for (int i = 1; i <= 20; i++)
        {
            // What an arrival hands out belongs to the frame before it.
            if (clock.Arrive(i / 60.0, out _) > 0)
            {
                kept.Add(i - 1);
            }
        }

        Assert.Equal(new[] { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18 }, kept);
    }

    [Fact]
    public void ZeroOrNegativeSpeed_TreatedAsNormal()
    {
        Assert.Equal(1.0, new LibavClipExporter.SourceRange(0, 1, 0).EffectiveSpeed);
        Assert.Equal(1.0, new LibavClipExporter.SourceRange(0, 1, -3).EffectiveSpeed);
    }

    [Fact]
    public void FactorChain_WithinRange_IsSingleFactor()
    {
        Assert.Equal(new[] { 1.5 }, LibavAtempoFilter.FactorChain(1.5));
        Assert.Equal(new[] { 0.5 }, LibavAtempoFilter.FactorChain(0.5));
        Assert.Equal(new[] { 2.0 }, LibavAtempoFilter.FactorChain(2.0));
    }

    [Fact]
    public void FactorChain_AboveTwo_ChainsToProductOfFactors()
    {
        double[] chain = System.Linq.Enumerable.ToArray(LibavAtempoFilter.FactorChain(4.0));
        Assert.All(chain, f => Assert.InRange(f, 0.5, 2.0));
        Assert.Equal(4.0, Product(chain), 6);
    }

    [Fact]
    public void FactorChain_BelowHalf_ChainsToProductOfFactors()
    {
        double[] chain = System.Linq.Enumerable.ToArray(LibavAtempoFilter.FactorChain(0.25));
        Assert.All(chain, f => Assert.InRange(f, 0.5, 2.0));
        Assert.Equal(0.25, Product(chain), 6);
    }

    private static double Product(double[] values)
    {
        double p = 1.0;
        foreach (double v in values)
        {
            p *= v;
        }
        return p;
    }
}
