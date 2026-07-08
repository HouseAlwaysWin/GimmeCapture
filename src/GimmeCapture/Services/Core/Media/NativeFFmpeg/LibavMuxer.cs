using System;
using System.Collections.Generic;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal static class LibavMuxer
{
    internal readonly record struct MuxStats(int VideoPackets, int AudioPackets);

    public static unsafe MuxStats MuxVideoAndAudioToMkv(string videoPath, string audioPath, string outputPath)
        => MuxVideoAndAudio(videoPath, audioPath, outputPath, "matroska");

    /// <summary>
    /// Concatenates multiple recorded video segments (produced back-to-back in the same
    /// session, e.g. after pause/resume) into a single output by copying packets without
    /// re-encoding. All segments must share the same codec/resolution, which holds because
    /// they originate from the same recording configuration.
    /// </summary>
    public static unsafe MuxStats ConcatVideoSegments(
        IReadOnlyList<string> segmentPaths,
        string outputPath,
        string containerFormat)
    {
        if (segmentPaths == null || segmentPaths.Count == 0)
        {
            throw new ArgumentException("No segments to concatenate.", nameof(segmentPaths));
        }

        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* outFmt = null;
        AVPacket* pkt = null;
        AVStream* outVideo = null;
        bool headerWritten = false;

        try
        {
            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, containerFormat, outputPath), "alloc_output(concat)");
            if (outFmt == null) throw new InvalidOperationException("Failed to create output format context.");

            pkt = ffmpeg.av_packet_alloc();
            if (pkt == null) throw new OutOfMemoryException("av_packet_alloc(concat)");

            // Running timestamp offset (in output time_base) so each segment starts where
            // the previous one ended, keeping PTS/DTS monotonic across boundaries.
            long nextOffset = 0;
            int totalVideoPackets = 0;

            foreach (var segmentPath in segmentPaths)
            {
                AVFormatContext* inFmt = null;
                try
                {
                    ThrowIfErr(ffmpeg.avformat_open_input(&inFmt, segmentPath, null, null), "open_input(concat)");
                    ThrowIfErr(ffmpeg.avformat_find_stream_info(inFmt, null), "find_stream_info(concat)");
                    int videoIn = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                    if (videoIn < 0)
                    {
                        // Skip segments without a usable video stream rather than aborting.
                        continue;
                    }

                    var inVideo = inFmt->streams[videoIn];

                    if (!headerWritten)
                    {
                        outVideo = ffmpeg.avformat_new_stream(outFmt, null);
                        if (outVideo == null) throw new OutOfMemoryException("new_stream(concat)");
                        ThrowIfErr(ffmpeg.avcodec_parameters_copy(outVideo->codecpar, inVideo->codecpar), "copy_video_codecpar(concat)");
                        outVideo->codecpar->codec_tag = 0;
                        outVideo->time_base = inVideo->time_base;
                        outVideo->disposition |= ffmpeg.AV_DISPOSITION_DEFAULT;

                        if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                        {
                            ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open(concat)");
                        }

                        ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "write_header(concat)");
                        headerWritten = true;
                    }

                    long offset = nextOffset;
                    bool hasPkt = ReadNextStreamPacket(inFmt, videoIn, pkt);
                    long firstPts = hasPkt ? GetPacketPrimaryTimestamp(pkt) : ffmpeg.AV_NOPTS_VALUE;
                    long segMaxEnd = 0;
                    int segPackets = 0;
                    bool sawDuration = false;

                    while (hasPkt)
                    {
                        // Re-base this segment to start at 0, convert to the output time_base,
                        // then push it past the previous segment with the running offset.
                        NormalizePacketTimestamps(pkt, firstPts);
                        ffmpeg.av_packet_rescale_ts(pkt, inVideo->time_base, outVideo->time_base);
                        if (pkt->pts != ffmpeg.AV_NOPTS_VALUE) pkt->pts += offset;
                        if (pkt->dts != ffmpeg.AV_NOPTS_VALUE) pkt->dts += offset;
                        pkt->stream_index = outVideo->index;

                        long endRef = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                        if (pkt->dts != ffmpeg.AV_NOPTS_VALUE && pkt->dts > endRef) endRef = pkt->dts;
                        if (endRef != ffmpeg.AV_NOPTS_VALUE)
                        {
                            long end = endRef + (pkt->duration > 0 ? pkt->duration : 0);
                            if (end > segMaxEnd) segMaxEnd = end;
                        }
                        if (pkt->duration > 0) sawDuration = true;

                        ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, pkt), "write_frame(concat)");
                        totalVideoPackets++;
                        segPackets++;
                        ffmpeg.av_packet_unref(pkt);
                        hasPkt = ReadNextStreamPacket(inFmt, videoIn, pkt);
                    }

                    // Advance the offset to the end of this segment. When packet durations
                    // are missing, segMaxEnd is the last frame's timestamp, so add one
                    // estimated frame to avoid overlapping the next segment's first frame.
                    if (segMaxEnd <= offset)
                    {
                        nextOffset = offset + 1;
                    }
                    else if (sawDuration)
                    {
                        nextOffset = segMaxEnd;
                    }
                    else
                    {
                        long avgFrameDur = segPackets > 1 ? (segMaxEnd - offset) / segPackets : 1;
                        nextOffset = segMaxEnd + Math.Max(avgFrameDur, 1);
                    }
                }
                finally
                {
                    if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
                }
            }

            if (!headerWritten)
            {
                throw new InvalidOperationException("No video stream found in any segment.");
            }

            ffmpeg.av_write_trailer(outFmt);
            return new MuxStats(totalVideoPackets, 0);
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }

                ffmpeg.avformat_free_context(outFmt);
            }
        }
    }

    public static unsafe MuxStats RemuxVideo(string videoPath, string outputPath, string containerFormat)
    {
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* videoFmt = null;
        AVFormatContext* outFmt = null;
        AVPacket* videoPkt = null;
        int videoIn = -1;
        AVStream* outVideo = null;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&videoFmt, videoPath, null, null), "open_input(video)");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(videoFmt, null), "find_stream_info(video)");
            videoIn = ffmpeg.av_find_best_stream(videoFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0) throw new InvalidOperationException("No video stream in input.");

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, containerFormat, outputPath), "alloc_output(video_only)");
            if (outFmt == null) throw new InvalidOperationException("Failed to create output format context.");

            outVideo = ffmpeg.avformat_new_stream(outFmt, null);
            if (outVideo == null) throw new OutOfMemoryException("new_stream(video_only)");

            var inVideoStream = videoFmt->streams[videoIn];
            ThrowIfErr(ffmpeg.avcodec_parameters_copy(outVideo->codecpar, inVideoStream->codecpar), "copy_video_codecpar(video_only)");
            outVideo->codecpar->codec_tag = 0;
            outVideo->time_base = inVideoStream->time_base;
            outVideo->disposition |= ffmpeg.AV_DISPOSITION_DEFAULT;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open(output_video_only)");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "write_header(output_video_only)");

            videoPkt = ffmpeg.av_packet_alloc();
            if (videoPkt == null) throw new OutOfMemoryException("av_packet_alloc(video_only)");

            int videoPackets = RemuxVideoPackets(videoFmt, videoIn, outFmt, outVideo, videoPkt);
            ffmpeg.av_write_trailer(outFmt);
            return new MuxStats(videoPackets, 0);
        }
        finally
        {
            if (videoPkt != null) ffmpeg.av_packet_free(&videoPkt);
            if (videoFmt != null) ffmpeg.avformat_close_input(&videoFmt);
            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }

                ffmpeg.avformat_free_context(outFmt);
            }
        }
    }

    /// <summary>
    /// Stream-copies every video + audio stream from a SINGLE input into a new container (no re-encode:
    /// lossless and fast, and it keeps the audio). Used for a pin's plain-container change (mp4↔mkv↔mov) so the
    /// output is a valid file of the chosen container instead of the source bytes under a mismatched extension.
    /// The source codecs must be acceptable in the target container (true for h264/h265/aac across mp4/mkv/mov);
    /// codec-incompatible changes (e.g. a GIF/WebM source → mp4) must re-encode instead.
    /// </summary>
    public static unsafe MuxStats RemuxAllStreams(string inputPath, string outputPath, string containerFormat)
    {
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* inFmt = null;
        AVFormatContext* outFmt = null;
        AVPacket* pkt = null;
        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null), "open_input(remux_all)");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(inFmt, null), "find_stream_info(remux_all)");

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, containerFormat, outputPath), "alloc_output(remux_all)");
            if (outFmt == null) throw new InvalidOperationException("Failed to create output format context.");

            int nStreams = (int)inFmt->nb_streams;
            int[] streamMap = new int[nStreams];
            int outIndex = 0;
            for (int i = 0; i < nStreams; i++)
            {
                streamMap[i] = -1;
                AVStream* inStream = inFmt->streams[i];
                AVMediaType type = inStream->codecpar->codec_type;
                // Copy only video + audio (drop data/subtitle streams the target container may reject).
                if (type != AVMediaType.AVMEDIA_TYPE_VIDEO && type != AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    continue;
                }

                AVStream* outStream = ffmpeg.avformat_new_stream(outFmt, null);
                if (outStream == null) throw new OutOfMemoryException("new_stream(remux_all)");
                ThrowIfErr(ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar), "copy_codecpar(remux_all)");
                outStream->codecpar->codec_tag = 0;
                streamMap[i] = outIndex++;
            }

            if (outIndex == 0) throw new InvalidOperationException("No video/audio streams to remux.");

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open(remux_all)");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "write_header(remux_all)");

            pkt = ffmpeg.av_packet_alloc();
            if (pkt == null) throw new OutOfMemoryException("av_packet_alloc(remux_all)");

            int videoPackets = 0;
            int audioPackets = 0;
            while (true)
            {
                int rr = ffmpeg.av_read_frame(inFmt, pkt);
                if (rr == ffmpeg.AVERROR_EOF) break;
                ThrowIfErr(rr, "read_frame(remux_all)");

                int src = pkt->stream_index;
                if (src < 0 || src >= nStreams || streamMap[src] < 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                AVStream* inStream = inFmt->streams[src];
                AVStream* outStream = outFmt->streams[streamMap[src]];
                ffmpeg.av_packet_rescale_ts(pkt, inStream->time_base, outStream->time_base);
                pkt->stream_index = streamMap[src];
                pkt->pos = -1;
                ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, pkt), "write_frame(remux_all)");
                if (inStream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoPackets++;
                }
                else
                {
                    audioPackets++;
                }
                ffmpeg.av_packet_unref(pkt); // safe no-op: av_interleaved_write_frame already reset the packet
            }

            ffmpeg.av_write_trailer(outFmt);
            return new MuxStats(videoPackets, audioPackets);
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }

                ffmpeg.avformat_free_context(outFmt);
            }
        }
    }

    public static unsafe MuxStats MuxVideoAndAudio(string videoPath, string audioPath, string outputPath, string containerFormat)
    {
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* videoFmt = null;
        AVFormatContext* audioFmt = null;
        AVFormatContext* outFmt = null;
        AVPacket* videoPkt = null;
        AVPacket* audioPkt = null;
        int videoIn = -1;
        int audioIn = -1;
        AVStream* outVideo = null;
        AVStream* outAudio = null;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&videoFmt, videoPath, null, null), "open_input(video)");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(videoFmt, null), "find_stream_info(video)");
            videoIn = ffmpeg.av_find_best_stream(videoFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0) throw new InvalidOperationException("No video stream in input.");

            ThrowIfErr(ffmpeg.avformat_open_input(&audioFmt, audioPath, null, null), "open_input(audio)");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(audioFmt, null), "find_stream_info(audio)");
            audioIn = ffmpeg.av_find_best_stream(audioFmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioIn < 0) throw new InvalidOperationException("No audio stream in input.");

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, containerFormat, outputPath), "alloc_output");
            if (outFmt == null) throw new InvalidOperationException("Failed to create output format context.");

            outVideo = ffmpeg.avformat_new_stream(outFmt, null);
            outAudio = ffmpeg.avformat_new_stream(outFmt, null);
            if (outVideo == null || outAudio == null) throw new OutOfMemoryException("new_stream");

            var inVideoStream = videoFmt->streams[videoIn];
            var inAudioStream = audioFmt->streams[audioIn];

            ThrowIfErr(ffmpeg.avcodec_parameters_copy(outVideo->codecpar, inVideoStream->codecpar), "copy_video_codecpar");
            ThrowIfErr(ffmpeg.avcodec_parameters_copy(outAudio->codecpar, inAudioStream->codecpar), "copy_audio_codecpar");
            outVideo->codecpar->codec_tag = 0;
            outAudio->codecpar->codec_tag = 0;
            outVideo->time_base = inVideoStream->time_base;
            outAudio->time_base = inAudioStream->time_base;
            outVideo->disposition |= ffmpeg.AV_DISPOSITION_DEFAULT;
            outAudio->disposition |= ffmpeg.AV_DISPOSITION_DEFAULT;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open(output)");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "write_header(output)");

            videoPkt = ffmpeg.av_packet_alloc();
            audioPkt = ffmpeg.av_packet_alloc();
            if (videoPkt == null || audioPkt == null) throw new OutOfMemoryException("av_packet_alloc");

            var stats = MuxInterleaved(videoFmt, videoIn, outFmt, outVideo, videoPkt, audioFmt, audioIn, outAudio, audioPkt);
            if (stats.AudioPackets <= 0)
            {
                throw new InvalidOperationException("Audio mux produced zero audio packets.");
            }

            ffmpeg.av_write_trailer(outFmt);
            return stats;
        }
        finally
        {
            if (videoPkt != null) ffmpeg.av_packet_free(&videoPkt);
            if (audioPkt != null) ffmpeg.av_packet_free(&audioPkt);
            if (videoFmt != null) ffmpeg.avformat_close_input(&videoFmt);
            if (audioFmt != null) ffmpeg.avformat_close_input(&audioFmt);
            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }
                ffmpeg.avformat_free_context(outFmt);
            }
        }
    }

    private static unsafe MuxStats MuxInterleaved(
        AVFormatContext* videoFmt, int videoIn, AVFormatContext* outFmt, AVStream* outVideo, AVPacket* videoPkt,
        AVFormatContext* audioFmt, int audioIn, AVStream* outAudio, AVPacket* audioPkt)
    {
        var inVideo = videoFmt->streams[videoIn];
        var inAudio = audioFmt->streams[audioIn];
        bool hasVideo = ReadNextStreamPacket(videoFmt, videoIn, videoPkt);
        bool hasAudio = ReadNextStreamPacket(audioFmt, audioIn, audioPkt);
        long firstVideoPts = hasVideo ? GetPacketPrimaryTimestamp(videoPkt) : ffmpeg.AV_NOPTS_VALUE;
        long firstAudioPts = hasAudio ? GetPacketPrimaryTimestamp(audioPkt) : ffmpeg.AV_NOPTS_VALUE;

        int videoPackets = 0;
        int audioPackets = 0;
        while (hasVideo || hasAudio)
        {
            bool takeVideo;
            if (!hasAudio) takeVideo = true;
            else if (!hasVideo) takeVideo = false;
            else
            {
                long vTs = PacketTsInUs(videoPkt, inVideo->time_base);
                long aTs = PacketTsInUs(audioPkt, inAudio->time_base);
                takeVideo = vTs <= aTs;
            }

            if (takeVideo)
            {
                NormalizePacketTimestamps(videoPkt, firstVideoPts);
                ffmpeg.av_packet_rescale_ts(videoPkt, inVideo->time_base, outVideo->time_base);
                videoPkt->stream_index = outVideo->index;
                ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, videoPkt), "write_frame(video)");
                videoPackets++;
                ffmpeg.av_packet_unref(videoPkt);
                hasVideo = ReadNextStreamPacket(videoFmt, videoIn, videoPkt);
            }
            else
            {
                NormalizePacketTimestamps(audioPkt, firstAudioPts);
                ffmpeg.av_packet_rescale_ts(audioPkt, inAudio->time_base, outAudio->time_base);
                audioPkt->stream_index = outAudio->index;
                ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, audioPkt), "write_frame(audio)");
                audioPackets++;
                ffmpeg.av_packet_unref(audioPkt);
                hasAudio = ReadNextStreamPacket(audioFmt, audioIn, audioPkt);
            }
        }
        return new MuxStats(videoPackets, audioPackets);
    }

    private static unsafe long GetPacketPrimaryTimestamp(AVPacket* pkt)
    {
        if (pkt == null)
        {
            return ffmpeg.AV_NOPTS_VALUE;
        }

        if (pkt->pts != ffmpeg.AV_NOPTS_VALUE && pkt->dts != ffmpeg.AV_NOPTS_VALUE)
        {
            return Math.Min(pkt->pts, pkt->dts);
        }

        if (pkt->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            return pkt->pts;
        }

        if (pkt->dts != ffmpeg.AV_NOPTS_VALUE)
        {
            return pkt->dts;
        }

        return ffmpeg.AV_NOPTS_VALUE;
    }

    private static unsafe void NormalizePacketTimestamps(AVPacket* pkt, long firstTimestamp)
    {
        if (firstTimestamp == ffmpeg.AV_NOPTS_VALUE)
        {
            return;
        }

        if (pkt->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            pkt->pts = Math.Max(0, pkt->pts - firstTimestamp);
        }

        if (pkt->dts != ffmpeg.AV_NOPTS_VALUE)
        {
            pkt->dts = Math.Max(0, pkt->dts - firstTimestamp);
        }
    }

    private static unsafe bool ReadNextStreamPacket(AVFormatContext* fmt, int streamIndex, AVPacket* pkt)
    {
        while (true)
        {
            int rr = ffmpeg.av_read_frame(fmt, pkt);
            if (rr == ffmpeg.AVERROR_EOF) return false;
            ThrowIfErr(rr, "read_frame(next)");
            if (pkt->stream_index == streamIndex) return true;
            ffmpeg.av_packet_unref(pkt);
        }
    }

    private static unsafe long PacketTsInUs(AVPacket* pkt, AVRational timeBase)
    {
        long ts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
        if (ts == ffmpeg.AV_NOPTS_VALUE) return long.MaxValue;
        var usTimeBase = new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE };
        return ffmpeg.av_rescale_q(ts, timeBase, usTimeBase);
    }

    private static unsafe int RemuxVideoPackets(
        AVFormatContext* videoFmt,
        int videoIn,
        AVFormatContext* outFmt,
        AVStream* outVideo,
        AVPacket* videoPkt)
    {
        var inVideo = videoFmt->streams[videoIn];
        bool hasVideo = ReadNextStreamPacket(videoFmt, videoIn, videoPkt);
        long firstVideoPts = hasVideo ? GetPacketPrimaryTimestamp(videoPkt) : ffmpeg.AV_NOPTS_VALUE;
        int videoPackets = 0;

        while (hasVideo)
        {
            NormalizePacketTimestamps(videoPkt, firstVideoPts);
            ffmpeg.av_packet_rescale_ts(videoPkt, inVideo->time_base, outVideo->time_base);
            videoPkt->stream_index = outVideo->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, videoPkt), "write_frame(video_only)");
            videoPackets++;
            ffmpeg.av_packet_unref(videoPkt);
            hasVideo = ReadNextStreamPacket(videoFmt, videoIn, videoPkt);
        }

        return videoPackets;
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0) throw FFmpegErrors.ToException(err, ctx);
    }
}
