using System;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// How many pixels a region recording's frames have, and how large a pin shows them so each lands on one of the
/// screen's. The recorder takes the region, in DIPs, at the screen's scaling and rounds it down to even, as encoders
/// need. The pin was made at the region's size in DIPs and decoded at that many pixels: at 125% a 1134x624 recording
/// played at 907x499, stretched back up to the screen.
/// </summary>
internal static class RecordingFrameSize
{
    /// <summary>The pixels a region <paramref name="dips"/> long is recorded at, at <paramref name="scaling"/>: even.</summary>
    public static int EvenPixels(double dips, double scaling) => ((int)(dips * scaling) / 2) * 2;

    /// <summary>
    /// The pin for a recording of a region <paramref name="dipWidth"/> x <paramref name="dipHeight"/> DIPs recorded at
    /// <paramref name="scaling"/>: the frames' pixels to decode at (2 at least, as the pin decodes), and the size in
    /// DIPs that shows them pixel for pixel on a screen at that scaling.
    /// </summary>
    public static (int PixelWidth, int PixelHeight, double DipWidth, double DipHeight) Pin(
        double dipWidth, double dipHeight, double scaling)
    {
        double safeScaling = scaling > 0 ? scaling : 1.0;
        int pixelWidth = Math.Max(2, EvenPixels(dipWidth, safeScaling));
        int pixelHeight = Math.Max(2, EvenPixels(dipHeight, safeScaling));
        return (pixelWidth, pixelHeight, pixelWidth / safeScaling, pixelHeight / safeScaling);
    }
}
