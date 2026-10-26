using System.Collections.Generic;

using GimmeCapture.Services.OCR;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Guards PaddleOCREngine.FindBoxesInProbabilityMap, the connected-component scan that turns the detection
// model's probability map into the boxes the recognizer then reads. It had no coverage at all before the map
// moved from float[h, w] / bool[h, w] to flat pooled arrays indexed y * w + x, so these tests pin the parts
// that decide what the OCR sees: the row-major indexing the new offsets rely on, the threshold, the 3px and
// minimum-area rejections, the expansion formula, and the all-false precondition on `visited` that the
// caller's Array.Clear of the rented buffer exists to satisfy.
public class PaddleOcrBlobDetectionTests
{
    // Writes a filled rectangle of above-threshold probability into a row-major map.
    private static float[] MakeMap(int h, int w, params (int X0, int Y0, int X1, int Y1)[] blobs)
    {
        var map = new float[h * w];
        foreach (var (x0, y0, x1, y1) in blobs)
        {
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    map[(y * w) + x] = 1f;
                }
            }
        }

        return map;
    }

    private static List<SKRectI> Detect(float[] map, int h, int w, int origW, int origH)
        => PaddleOCREngine.FindBoxesInProbabilityMap(map, new bool[h * w], h, w, origW, origH);

    [Fact]
    public void OneBlob_ProducesOneBoxWithTheExpansionFormulaApplied()
    {
        // A 5x5 blob at (5,5)-(9,9) on a 20x20 map scaled 10x to a 200x200 image.
        // area 25, blobW/H 5 -> expansion = 25 * 1.6 / (2 * (5 + 5)) = 2.
        // x: (5 - 2) * 10 = 30, w: (5 + 4) * 10 = 90.
        var boxes = Detect(MakeMap(20, 20, (5, 5, 9, 9)), 20, 20, 200, 200);

        Assert.Single(boxes);
        Assert.Equal(new SKRectI(30, 30, 120, 120), boxes[0]);
    }

    [Fact]
    public void TwoSeparatedBlobs_ProduceTwoBoxes()
    {
        var boxes = Detect(MakeMap(20, 20, (2, 2, 6, 6), (12, 12, 16, 16)), 20, 20, 200, 200);

        Assert.Equal(2, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.Width > 6 && b.Height > 6));
    }

    [Fact]
    public void NonSquareMap_IndexesRowMajor_SoWidthAndHeightAreNotTransposed()
    {
        // h != w, and the blob sits where a transposed index (x * h + y) would read a different pixel:
        // a swap would move or lose the box entirely. Blob x 12..16, y 2..6 on a 10-row, 20-column map.
        var boxes = Detect(MakeMap(10, 20, (12, 2, 16, 6)), 10, 20, 200, 100);

        Assert.Single(boxes);
        // scaleX = 200/20 = 10, scaleY = 100/10 = 10, expansion = 2 as above.
        Assert.Equal(new SKRectI(100, 0, 190, 90), boxes[0]);
    }

    [Fact]
    public void BlobThinnerThanThreePixels_IsRejected()
    {
        // blobW == 2 trips the `blobW < 3` guard, so nothing is reported and the caller falls back
        // to the whole frame.
        var boxes = Detect(MakeMap(20, 20, (5, 5, 6, 9)), 20, 20, 200, 200);

        Assert.Empty(boxes);
    }

    [Fact]
    public void BelowThresholdProbabilities_AreNotBlobs()
    {
        var map = new float[20 * 20];
        for (int i = 0; i < map.Length; i++)
        {
            map[i] = 0.3f; // DetBoxThreshold is 0.3 and the comparison is strictly greater
        }

        Assert.Empty(PaddleOCREngine.FindBoxesInProbabilityMap(map, new bool[20 * 20], 20, 20, 200, 200));
    }

    [Fact]
    public void StaleVisitedBuffer_SuppressesABlob_WhichIsWhyTheRentedOneIsCleared()
    {
        var map = MakeMap(20, 20, (5, 5, 9, 9));
        Assert.Single(Detect(map, 20, 20, 200, 200));

        // Same map, but handed a buffer that still reads "visited" from a previous scan — what
        // ArrayPool.Rent returns without the caller's Array.Clear. The blob disappears silently.
        var dirty = new bool[20 * 20];
        for (int i = 0; i < dirty.Length; i++)
        {
            dirty[i] = true;
        }

        Assert.Empty(PaddleOCREngine.FindBoxesInProbabilityMap(map, dirty, 20, 20, 200, 200));
    }
}
