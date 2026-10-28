using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Guards SAM2Service.ReadBestMask, which picks the decoder's best mask for a click and reads it out. It reads the masks
// through the tensor's memory, at offsets from the tensor's strides, where it used the tensor's own indexer: a params
// array and a trip through ONNX Runtime's memory manager for every one of about four million reads a click. The answer
// must not change, so every case here is held to IndexerReadout, the readout as it was, through the indexer.
//
// The masks are 37 rows by 23 columns, three of them: reading x as y, or a row's stride as the mask's, or the wrong
// mask, reads other values. Each mask selects a rectangle, and the points sit in the middle of mask cells, so which
// mask wins and why is known for each case.
public class Sam2MaskReadoutTests
{
    private const int Masks = 3;
    private const int Rows = 37;
    private const int Columns = 23;

    // The click's image is ten pixels to a mask cell, so (cell + 0.5) * 10 lands in the middle of the cell.
    private const int ImageWidth = Columns * 10;
    private const int ImageHeight = Rows * 10;
    private const float ScaleX = 1024f / ImageWidth;
    private const float ScaleY = 1024f / ImageHeight;

    // Rows top..bottom and columns left..right, inclusive.
    private static readonly (int Top, int Bottom, int Left, int Right)[] Rectangles =
    [
        (5, 15, 3, 10),  // 88 of 851 cells: the only mask that fits the points below
        (2, 30, 2, 20),  // 551 cells: holds both negative points
        (10, 25, 8, 18), // 176 cells: misses the positive point and holds a negative one
    ];

    private static readonly (double X, double Y, bool IsPositive)[] Points =
    [
        Point(row: 8, column: 5, positive: true),
        Point(row: 20, column: 12, positive: false),
        // Outside the first mask, but its row and column swapped lie inside it: the readout tags it [SWP?].
        Point(row: 3, column: 7, positive: false),
    ];

    private static readonly float[] Ious = [0.71f, 0.93f, 0.85f];

    /// <summary>The three kinds of value the readout tells apart by their range, each with its own threshold.</summary>
    public enum ValueRange
    {
        Logits,        // threshold 0
        Probabilities, // threshold 0.5
        Bytes,         // threshold 127.5
    }

    [Theory]
    [InlineData(ValueRange.Logits, false)]
    [InlineData(ValueRange.Probabilities, false)]
    [InlineData(ValueRange.Bytes, false)]
    [InlineData(ValueRange.Logits, true)] // stored column-major: only the strides say where a value lies
    public void PicksAndReadsTheSameMaskAsTheIndexer(ValueRange range, bool reverseStride)
    {
        var masks = RectangleMasks(range, reverseStride, Rectangles);

        var expected = IndexerReadout(masks, Iou(), Points);
        using var mask = SAM2Service.ReadBestMask(masks, Iou(), Points, ScaleX, ScaleY, out var choice);

        Assert.Equal(0, expected.Index);
        Assert.Contains("[SWP?]", expected.PointStats);
        AssertSameReadout(expected, choice, mask);
    }

    [Fact]
    public void InvertsAWinningMaskThatSelectsNearlyEverything_AsTheIndexerDid()
    {
        // The first mask selects all but a 6 x 5 hole, corners included; the other two miss the positive point.
        var masks = RectangleMasks(ValueRange.Logits, reverseStride: false, (0, Rows - 1, 0, Columns - 1), (20, 30, 15, 20), (25, 35, 1, 6));
        for (int y = 15; y <= 20; y++)
        {
            for (int x = 8; x <= 12; x++)
            {
                masks[0, 0, y, x] = -5f;
            }
        }

        (double X, double Y, bool IsPositive)[] points = [Point(row: 5, column: 5, positive: true)];

        var expected = IndexerReadout(masks, Iou(), points);
        using var mask = SAM2Service.ReadBestMask(masks, Iou(), points, ScaleX, ScaleY, out var choice);

        Assert.True(expected.Inverted);
        AssertSameReadout(expected, choice, mask);
    }

    private static void AssertSameReadout(IndexerResult expected, SAM2Service.MaskChoice choice, SKBitmap mask)
    {
        Assert.Equal(expected.Index, choice.Index);
        Assert.Equal(expected.Inverted, choice.Inverted);
        Assert.Equal(expected.Density, choice.Density);
        Assert.Equal(expected.PointStats, choice.PointStats);

        Assert.Equal(SKColorType.Gray8, mask.ColorType);
        Assert.Equal(Columns, mask.Width);
        Assert.Equal(Rows, mask.Height);
        Assert.Equal(expected.Pixels, TestBitmaps.ReadPixels<byte>(mask));
    }

    private static (double X, double Y, bool IsPositive) Point(int row, int column, bool positive) =>
        ((column + 0.5) * 10, (row + 0.5) * 10, positive);

    private static DenseTensor<float> Iou() => new(Ious.AsMemory(), new[] { 1, Masks });

    /// <summary>
    /// Masks shaped like the decoder's output (1 x masks x rows x columns), each high inside its rectangle and low
    /// outside, by a different amount at each cell.
    /// </summary>
    private static DenseTensor<float> RectangleMasks(
        ValueRange range, bool reverseStride, params (int Top, int Bottom, int Left, int Right)[] rectangles)
    {
        var tensor = new DenseTensor<float>(new[] { 1, rectangles.Length, Rows, Columns }, reverseStride);
        for (int m = 0; m < rectangles.Length; m++)
        {
            var (top, bottom, left, right) = rectangles[m];
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++)
                {
                    bool inside = y >= top && y <= bottom && x >= left && x <= right;
                    float jitter = (TestBitmaps.Noise(x, y, m) % 1000) / 1000f; // 0 to 1
                    tensor[0, m, y, x] = range switch
                    {
                        ValueRange.Logits => inside ? 4f + (4f * jitter) : -4f - (4f * jitter),
                        ValueRange.Probabilities => inside ? 0.8f + (0.15f * jitter) : 0.2f - (0.15f * jitter),
                        _ => inside ? 200f + (50f * jitter) : 40f + (50f * jitter),
                    };
                }
            }
        }

        return tensor;
    }

    private sealed record IndexerResult(int Index, bool Inverted, float Density, string PointStats, byte[] Pixels);

    /// <summary>
    /// The readout as it was before it read the tensor's memory: the same rules, every value read through the tensor's
    /// indexer. Kept apart from the app's code on purpose, so a change to the rules there cannot pass by changing both.
    /// </summary>
    private static IndexerResult IndexerReadout(
        Tensor<float> masksTensor, Tensor<float> iouTensor, IReadOnlyList<(double X, double Y, bool IsPositive)> pointList)
    {
        int bestIdx = 0;
        float maxScore = -1000f;
        float bestDensity = 0;
        string bestPointStats = "";
        int numMasks = iouTensor.Dimensions[1];
        int mh = masksTensor.Dimensions[2], mw = masksTensor.Dimensions[3];
        float gxFactor = (float)mw / 1024f, gyFactor = (float)mh / 1024f;

        for (int i = 0; i < numMasks; i++)
        {
            float minVal = float.MaxValue, maxVal = float.MinValue;
            for (int sy = 0; sy < mh; sy += 10)
            {
                for (int sx = 0; sx < mw; sx += 10)
                {
                    float v = masksTensor[0, i, sy, sx];
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            float threshold = (maxVal > 10) ? 127.5f : (minVal < -1 ? 0f : 0.5f);

            float iou = iouTensor[0, i];
            int pointErrors = 0;
            string pointStats = $"R:{minVal:F0}~{maxVal:F0}T:{threshold:F1} ";
            for (int pIdx = 0; pIdx < pointList.Count; pIdx++)
            {
                var p = pointList[pIdx];
                int gx = (int)Math.Clamp(p.X * ScaleX * gxFactor, 0, mw - 1);
                int gy = (int)Math.Clamp(p.Y * ScaleY * gyFactor, 0, mh - 1);

                float val = masksTensor[0, i, gy, gx];
                float valSwap = masksTensor[0, i, Math.Clamp(gx, 0, mh - 1), Math.Clamp(gy, 0, mw - 1)];

                bool hasMask = val > threshold;
                if (p.IsPositive && !hasMask) pointErrors++;
                if (!p.IsPositive && hasMask) pointErrors++;

                string xyTag = (val <= threshold && valSwap > threshold) ? "[SWP?]" : "";
                pointStats += $"P{pIdx}({gx},{gy})={val:F0}{xyTag}|";

                if (i == 0 && pIdx == 0)
                {
                    bestPointStats = pointStats;
                }
            }

            int count = 0;
            for (int y = 0; y < mh; y++)
                for (int x = 0; x < mw; x++)
                    if (masksTensor[0, i, Math.Min(y, mh - 1), Math.Min(x, mw - 1)] > threshold) count++;

            float density = (float)count / Math.Max(1, mw * mh);
            float currentScore = iou;
            if (pointErrors > 0) currentScore -= (pointErrors * 50.0f);
            if (density > 0.45f) currentScore -= 10.0f;
            else if (density < 0.25f && density > 0.01f) currentScore += 2.0f;

            if (currentScore > maxScore)
            {
                maxScore = currentScore; bestIdx = i; bestDensity = density; bestPointStats = pointStats;
            }
        }

        float finalMinVal = float.MaxValue, finalMaxVal = float.MinValue;
        for (int sy = 0; sy < mh; sy += 5)
        {
            for (int sx = 0; sx < mw; sx += 5)
            {
                float v = masksTensor[0, bestIdx, sy, sx];
                if (v < finalMinVal) finalMinVal = v;
                if (v > finalMaxVal) finalMaxVal = v;
            }
        }

        float finalThreshold = (finalMaxVal > 10) ? 127.5f : (finalMinVal < -1 ? 0f : 0.5f);

        bool isAllSelect = masksTensor[0, bestIdx, 1, 1] > finalThreshold
            && masksTensor[0, bestIdx, 1, mw - 2] > finalThreshold
            && masksTensor[0, bestIdx, mh - 2, 1] > finalThreshold
            && masksTensor[0, bestIdx, mh - 2, mw - 2] > finalThreshold;
        bool invertMask = isAllSelect && bestDensity > 0.7f;

        var pixels = new byte[mw * mh];
        for (int y = 0; y < mh; y++)
        {
            for (int x = 0; x < mw; x++)
            {
                bool isSelected = masksTensor[0, bestIdx, y, x] > finalThreshold;
                if (invertMask) isSelected = !isSelected;
                pixels[(y * mw) + x] = isSelected ? (byte)255 : (byte)0;
            }
        }

        return new IndexerResult(bestIdx, invertMask, bestDensity, bestPointStats, pixels);
    }
}
