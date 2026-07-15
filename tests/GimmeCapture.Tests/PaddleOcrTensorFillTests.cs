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
