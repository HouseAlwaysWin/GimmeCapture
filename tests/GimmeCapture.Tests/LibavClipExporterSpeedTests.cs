using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

public class LibavClipExporterSpeedTests
{
    private static int TotalEmitted(double speed, int sourceFrames)
    {
        double accum = 0;
        int total = 0;
        for (int i = 0; i < sourceFrames; i++)
        {
            total += LibavClipExporter.AdvanceFrameCursor(ref accum, speed);
        }
        return total;
    }

    [Fact]
    public void NormalSpeed_EmitsExactlyOnePerFrame()
    {
        double accum = 0;
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(1, LibavClipExporter.AdvanceFrameCursor(ref accum, 1.0));
        }
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
    public void ZeroOrNegativeSpeed_TreatedAsNormal()
    {
        double accum = 0;
        Assert.Equal(1, LibavClipExporter.AdvanceFrameCursor(ref accum, 0));
        Assert.Equal(1, LibavClipExporter.AdvanceFrameCursor(ref accum, -3));
    }
}
