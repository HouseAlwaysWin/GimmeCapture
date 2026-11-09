using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>Opens a user's media file for reading.</summary>
internal static unsafe class LibavInput
{
    /// <summary>
    /// <c>avformat_open_input</c>, but an MP4 whose media data is split over many top-level atoms is opened without reading
    /// the header of each one first (<see cref="Mp4Layout"/>). Its index is complete, so <c>AVFMT_FLAG_IGNIDX</c>, which in
    /// that reader means only "stop at the first media data", changes nothing about its frames: those atoms are walked as
    /// reading comes to them instead. On four such files of the user's, compared packet by packet from the start and after
    /// seeks to 25, 50 and 90%, the same packets came out, an open took 38-93 reads instead of 1,503-3,225, and a seek
    /// far ahead paid for the atoms it passed, once. Only this one reader and layout get the flag: it would make others
    /// ignore their indexes.
    /// </summary>
    internal static int Open(AVFormatContext** format, string path, AVDictionary** options = null)
    {
        if (Mp4Layout.HasIndexThenSplitMediaData(path))
        {
            AVFormatContext* context = ffmpeg.avformat_alloc_context();
            if (context != null)
            {
                context->flags |= ffmpeg.AVFMT_FLAG_IGNIDX;
                *format = context; // avformat_open_input frees it if the open fails
            }
        }

        return ffmpeg.avformat_open_input(format, path, null, options);
    }
}
