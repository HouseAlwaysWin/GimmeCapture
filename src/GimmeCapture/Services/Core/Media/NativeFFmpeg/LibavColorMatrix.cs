using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Which YUV matrix and range a picture holds, for swscale, which took every picture for limited-range BT.601 whatever
/// its stream said. Most HD video says BT.709 (a phone's, a camera's, another recorder's, and the app's own recordings
/// on AMD's encoder, which writes BT.709 whatever it is told): read as BT.601, its reds came out darker (255,0,0 as
/// 231,0,1) and its greens yellower (0,255,0 as 19,255,6) in a pin, the editor, a GIF and a thumbnail. A picture that
/// says nothing is still read as BT.601, which is what the app's own recordings were until they said so.
/// </summary>
internal static unsafe class LibavColorMatrix
{
    /// <summary>The swscale matrix for a stream's colour space: BT.709 or BT.2020 where it says so, BT.601 otherwise.</summary>
    internal static int SwsMatrix(AVColorSpace space) => space switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => ffmpeg.SWS_CS_BT2020,
        _ => ffmpeg.SWS_CS_DEFAULT,
    };

    /// <summary>Sets a scaler that turns YUV pictures into RGB to read them with the matrix and range they hold.</summary>
    public static void ReadAs(SwsContext* scaler, AVColorSpace space, AVColorRange range)
    {
        int matrix = SwsMatrix(space);
        bool fullRange = range == AVColorRange.AVCOL_RANGE_JPEG;
        if (scaler == null || (matrix == ffmpeg.SWS_CS_DEFAULT && !fullRange))
        {
            return; // what swscale assumes
        }

        int* sourceTable;
        int* targetTable;
        int sourceRange, targetRange, brightness, contrast, saturation;
        if (ffmpeg.sws_getColorspaceDetails(
                scaler, &sourceTable, &sourceRange, &targetTable, &targetRange, &brightness, &contrast, &saturation) < 0)
        {
            return; // not a YUV-to-RGB scaler
        }

        int_array4 source = Coefficients(matrix);
        int_array4 target = Copy(targetTable);
        ffmpeg.sws_setColorspaceDetails(
            scaler, ref source, fullRange ? 1 : sourceRange, ref target, targetRange, brightness, contrast, saturation);
    }

    /// <summary>Sets a scaler that turns RGB pictures into YUV to write the matrix and range an encoder declares.</summary>
    public static void WriteAs(SwsContext* scaler, AVColorSpace space, AVColorRange range)
    {
        int matrix = SwsMatrix(space);
        bool fullRange = range == AVColorRange.AVCOL_RANGE_JPEG;
        if (scaler == null || (matrix == ffmpeg.SWS_CS_DEFAULT && !fullRange))
        {
            return; // what swscale writes
        }

        int* sourceTable;
        int* targetTable;
        int sourceRange, targetRange, brightness, contrast, saturation;
        if (ffmpeg.sws_getColorspaceDetails(
                scaler, &sourceTable, &sourceRange, &targetTable, &targetRange, &brightness, &contrast, &saturation) < 0)
        {
            return; // not an RGB-to-YUV scaler
        }

        int_array4 source = Copy(sourceTable);
        int_array4 target = Coefficients(matrix);
        ffmpeg.sws_setColorspaceDetails(
            scaler, ref source, sourceRange, ref target, fullRange ? 1 : targetRange, brightness, contrast, saturation);
    }

    private static int_array4 Coefficients(int matrix) => Copy(ffmpeg.sws_getCoefficients(matrix));

    private static int_array4 Copy(int* table)
    {
        var copy = new int_array4();
        for (uint i = 0; i < 4; i++)
        {
            copy[i] = table[i];
        }

        return copy;
    }
}
