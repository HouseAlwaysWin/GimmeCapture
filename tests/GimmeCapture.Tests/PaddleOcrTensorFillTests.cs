using GimmeCapture.Services.OCR;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Guards the flat-buffer tensor optimizations in PaddleOCREngine (FillDetectionTensor,
// FillRecognitionTensor, DecodeCTC): they write/read DenseTensor.Buffer.Span with hand-computed
// row-major offsets instead of the multi-dimensional indexer. These tests prove (a) the layout
// assumption the offsets rely on, and (b) that the fills produce exactly the normalized values the
// old indexer-based fills did (readable back through the indexer).
public class PaddleOcrTensorFillTests
{
    private static SKBitmap MakeBitmap(int w, int h)
    {
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bmp.SetPixel(x, y, new SKColor(
                    (byte)((x * 17 + y * 3) & 0xFF),
                    (byte)((x * 5 + y * 11) & 0xFF),
                    (byte)((x * 29 + y * 7) & 0xFF),
                    255));
            }
        }

        return bmp;
    }

    [Fact]
    public void DenseTensor_4D_IsRowMajor()
    {
        // FillDetectionTensor / FillRecognitionTensor rely on buffer[c*H*W + y*W + x] == tensor[0,c,y,x].
        int c = 3, h = 4, w = 5, plane = h * w;
        var t = new DenseTensor<float>(new[] { 1, c, h, w });
        for (int cc = 0; cc < c; cc++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    t[0, cc, y, x] = (cc * 1000) + (y * 10) + x;
                }
            }
        }

        var span = t.Buffer.Span;
        for (int cc = 0; cc < c; cc++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Assert.Equal(t[0, cc, y, x], span[(cc * plane) + (y * w) + x]);
                }
            }
        }
    }

    [Fact]
    public void DenseTensor_3D_IsRowMajor()
    {
        // DecodeCTC relies on [0,i,j] == buffer[i*d2 + j] for a [1,d1,d2] tensor (NTC: flat[t*stride+c],
        // NCT: flat[c*stride+t]).
        int d1 = 4, d2 = 7;
        var t = new DenseTensor<float>(new[] { 1, d1, d2 });
        for (int i = 0; i < d1; i++)
        {
            for (int j = 0; j < d2; j++)
            {
                t[0, i, j] = (i * 100) + j;
            }
        }

        var span = t.Buffer.Span;
        for (int i = 0; i < d1; i++)
        {
            for (int j = 0; j < d2; j++)
            {
                Assert.Equal(t[0, i, j], span[(i * d2) + j]);
            }
        }
    }

    [Fact]
    public void FillDetectionTensor_MatchesIndexerNormalization()
    {
        int w = 8, h = 5;
        using var bmp = MakeBitmap(w, h);
        var input = new DenseTensor<float>(new[] { 1, 3, h, w });
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        PaddleOCREngine.FillDetectionTensor(bmp, input, mean, std);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor px = bmp.GetPixel(x, y);
                float red = px.Red / 255.0f;
                float green = px.Green / 255.0f;
                float blue = px.Blue / 255.0f;
                Assert.Equal((red - mean[0]) / std[0], input[0, 0, y, x], 6);
                Assert.Equal((green - mean[1]) / std[1], input[0, 1, y, x], 6);
                Assert.Equal((blue - mean[2]) / std[2], input[0, 2, y, x], 6);
            }
        }
    }

    // Every layout FillDetProbabilityMap's switch handles, in the order it tests them.
    public static TheoryData<int[]> DetectionMaskLayouts => new()
    {
        new[] { 1, 1, 5, 7 },  // NCHW, one channel
        new[] { 1, 5, 7, 1 },  // NHWC, one channel
        new[] { 1, 2, 5, 7 },  // NCHW, channel 0 of two
        new[] { 1, 5, 7 },     // a leading 1, then H and W
        new[] { 5, 7, 2 },     // HWC, channel 0 of two: values two apart
        new[] { 5, 7, 1 },     // HWC, one channel
        new[] { 5, 7 },        // H and W alone
    };

    [Theory]
    [MemberData(nameof(DetectionMaskLayouts))]
    public void FillDetProbabilityMap_ReadsTheBufferAsTheIndexerDid(int[] dimensions)
    {
        var mask = new DenseTensor<float>(dimensions);
        FillWithDistinctScores(mask.Buffer.Span);

        Assert.True(PaddleOCREngine.TryBuildDetProbabilityMap(mask, out float[] probMap, out int h, out int w));
        try
        {
            var expected = new float[h * w];
            PaddleOCREngine.FillDetProbabilityMapWithIndexer(mask, expected, h, w);

            Assert.Equal(expected, probMap.AsSpan(0, h * w).ToArray());
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(probMap);
        }
    }

    [Fact]
    public void FillDetProbabilityMap_ReadsAColumnMajorTensorThroughTheIndexer()
    {
        // Column-major puts the same values at other offsets: read as row-major, its buffer would scramble the map.
        var mask = new DenseTensor<float>(new[] { 1, 1, 5, 7 }, reverseStride: true);
        FillWithDistinctScores(mask.Buffer.Span);
        var expected = new float[5 * 7];
        var actual = new float[5 * 7];

        PaddleOCREngine.FillDetProbabilityMapWithIndexer(mask, expected, 5, 7);
        PaddleOCREngine.FillDetProbabilityMap(mask, actual, 5, 7);

        Assert.Equal(expected, actual);
    }

    // Probabilities below 0.5 and logits whose sigmoid is above 0.95, all different, so a misplaced read or a skipped
    // sigmoid shows.
    private static void FillWithDistinctScores(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = i % 2 == 0 ? i / (2f * buffer.Length) : 3f + i;
        }
    }

    [Fact]
    public void FillRecognitionTensor_MatchesIndexerNormalization()
    {
        int w = 6, h = 4;
        using var bmp = MakeBitmap(w, h);
        var input = new DenseTensor<float>(new[] { 1, 3, h, w });

        PaddleOCREngine.FillRecognitionTensor(bmp, input);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                SKColor px = bmp.GetPixel(x, y);
                Assert.Equal(((px.Red / 255.0f) - 0.5f) / 0.5f, input[0, 0, y, x], 6);
                Assert.Equal(((px.Green / 255.0f) - 0.5f) / 0.5f, input[0, 1, y, x], 6);
                Assert.Equal(((px.Blue / 255.0f) - 0.5f) / 0.5f, input[0, 2, y, x], 6);
            }
        }
    }
}
