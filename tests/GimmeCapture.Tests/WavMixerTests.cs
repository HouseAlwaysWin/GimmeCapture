using System;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public class WavMixerTests
{
    [Fact]
    public void MixBlock_SumsEqualLengthBlocks_WithUnityGain()
    {
        float[] a = { 0.1f, -0.2f, 0.3f };
        float[] b = { 0.2f, 0.2f, -0.1f };
        float[] dest = new float[3];

        int n = WavMixer.MixBlock(a, 3, b, 3, 1.0f, dest);

        Assert.Equal(3, n);
        Assert.Equal(0.3f, dest[0], 5);
        Assert.Equal(0.0f, dest[1], 5);
        Assert.Equal(0.2f, dest[2], 5);
    }

    [Fact]
    public void MixBlock_AppliesGainToSecondSourceOnly()
    {
        float[] a = { 0.1f, 0.1f };
        float[] b = { 0.4f, 0.4f };
        float[] dest = new float[2];

        WavMixer.MixBlock(a, 2, b, 2, 0.5f, dest);

        // a + b*0.5 = 0.1 + 0.2 = 0.3
        Assert.Equal(0.3f, dest[0], 5);
        Assert.Equal(0.3f, dest[1], 5);
    }

    [Fact]
    public void MixBlock_ClipsToUnitRange()
    {
        float[] a = { 0.9f, -0.9f };
        float[] b = { 0.9f, -0.9f };
        float[] dest = new float[2];

        WavMixer.MixBlock(a, 2, b, 2, 1.0f, dest);

        Assert.Equal(1.0f, dest[0], 5);   // 1.8 clipped to 1.0
        Assert.Equal(-1.0f, dest[1], 5);  // -1.8 clipped to -1.0
    }

    [Fact]
    public void MixBlock_TreatsShorterSourceAsSilence_AndReturnsMaxLength()
    {
        float[] a = { 0.5f, 0.5f, 0.5f, 0.5f };
        float[] b = { 0.25f, 0.25f };
        float[] dest = new float[4];

        int n = WavMixer.MixBlock(a, 4, b, 2, 1.0f, dest);

        Assert.Equal(4, n);
        Assert.Equal(0.75f, dest[0], 5);  // a + b
        Assert.Equal(0.75f, dest[1], 5);  // a + b
        Assert.Equal(0.5f, dest[2], 5);   // a only (b is silent)
        Assert.Equal(0.5f, dest[3], 5);   // a only
    }

    [Fact]
    public void MixBlock_HandlesSecondSourceLonger()
    {
        float[] a = { 0.1f };
        float[] b = { 0.2f, 0.3f, 0.4f };
        float[] dest = new float[3];

        int n = WavMixer.MixBlock(a, 1, b, 3, 1.0f, dest);

        Assert.Equal(3, n);
        Assert.Equal(0.3f, dest[0], 5);   // a + b
        Assert.Equal(0.3f, dest[1], 5);   // b only
        Assert.Equal(0.4f, dest[2], 5);   // b only
    }

    [Fact]
    public void MixBlock_ThrowsWhenDestinationTooSmall()
    {
        float[] a = { 0.1f, 0.2f, 0.3f };
        float[] b = { 0.1f };
        float[] dest = new float[2];

        Assert.Throws<ArgumentException>(() => WavMixer.MixBlock(a, 3, b, 1, 1.0f, dest));
    }
}
