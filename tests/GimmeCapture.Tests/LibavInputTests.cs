using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

public sealed unsafe class LibavInputTests : IDisposable
{
    // Detailed enough for more than 1 MB of picture, which is when the muxer starts a second chunk to split at.
    private const int Width = 1280;
    private const int Height = 720;
    private const int Frames = 45;
    private const int Fps = 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", nameof(LibavInputTests), Guid.NewGuid().ToString("N"));

    public LibavInputTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void AnIndexThenSplitMediaData_IsNotWalkedAtOpen_AndGivesTheSamePackets()
    {
        string path = WriteIndexFirst("split.mp4");
        SplitMediaDataInTwo(path);

        using (var viaInput = OpenedFile.ThroughInput(path))
        {
            Assert.True((viaInput.Format->flags & ffmpeg.AVFMT_FLAG_IGNIDX) != 0, "the file should not be walked at open");
            using var plain = OpenedFile.Plain(path);
            Assert.Equal(plain.Format->duration, viaInput.Format->duration);

            Assert.Equal(plain.ReadPackets(int.MaxValue), viaInput.ReadPackets(int.MaxValue));
        }

        foreach (double fraction in new[] { 0.5, 0.9, 0.25 })
        {
            using var viaInput = OpenedFile.ThroughInput(path);
            using var plain = OpenedFile.Plain(path);
            Assert.Equal(plain.ReadPacketsFrom(fraction), viaInput.ReadPacketsFrom(fraction));
        }
    }

    [Fact]
    public void AFileWithOneMediaAtom_OpensAsAnyOther()
    {
        using var opened = OpenedFile.ThroughInput(WriteIndexFirst("single.mp4"));
        Assert.True((opened.Format->flags & ffmpeg.AVFMT_FLAG_IGNIDX) == 0);
    }

    [Fact]
    public void AnotherFormat_OpensAsAnyOther_ItsIndexIsNotIgnored()
    {
        using var opened = OpenedFile.ThroughInput(VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), Width, Height, Frames, Fps, gopSize: 10));
        Assert.True((opened.Format->flags & ffmpeg.AVFMT_FLAG_IGNIDX) == 0);
    }

    [Fact]
    public void AFileThatCannotBeOpened_ReportsItAndLeavesNothingBehind()
    {
        string path = Path.Combine(_dir, "broken.mp4");
        File.WriteAllBytes(path, Atoms(("ftyp", 12), ("moov", 16), ("mdat", 16), ("mdat", 16)));

        AVFormatContext* format = null;
        Assert.True(LibavInput.Open(&format, path) < 0);
        Assert.True(format == null);
    }

    private static byte[] Atoms(params (string Type, int Size)[] atoms)
    {
        var bytes = new byte[atoms.Sum(a => a.Size)];
        int at = 0;
        foreach (var (type, size) in atoms)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)size);
            System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(at + 4));
            at += size;
        }

        return bytes;
    }

    private string WriteIndexFirst(string name)
    {
        string path = Path.Combine(_dir, name);
        string written = VideoTestClips.WriteConstantRate(path + ".mp4", Width, Height, Frames, Fps, detailed: true, gopSize: 10);
        MoveIndexFirst(written, path);
        return path;
    }

    // ftyp, [free,] mdat, moov becomes ftyp, moov, mdat, with the index's chunk offsets moved along.
    private static void MoveIndexFirst(string from, string to)
    {
        byte[] file = File.ReadAllBytes(from);
        var atoms = TopLevel(file);
        var moov = atoms.Single(a => a.Type == "moov");
        var mdat = atoms.Single(a => a.Type == "mdat");
        var ftyp = atoms.Single(a => a.Type == "ftyp");
        byte[] index = file.AsSpan(moov.Start, moov.Size).ToArray();

        int shift = (ftyp.Size + index.Length) - mdat.Start;
        foreach ((int entries, int count) in ChunkOffsets(index))
        {
            for (int i = 0; i < count; i++)
            {
                int at = entries + (i * 4);
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(at), (uint)((int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(at)) + shift));
            }
        }

        using FileStream output = File.Create(to);
        output.Write(file, ftyp.Start, ftyp.Size);
        output.Write(index);
        output.Write(file, mdat.Start, mdat.Size);
    }

    // ftyp, moov, mdat becomes ftyp, moov, mdat, mdat: the second starts at the middle chunk, as a real file's would.
    private static void SplitMediaDataInTwo(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        var atoms = TopLevel(file);
        var moov = atoms.Single(a => a.Type == "moov");
        var mdat = atoms.Single(a => a.Type == "mdat");
        byte[] index = file.AsSpan(moov.Start, moov.Size).ToArray();
        (int entries, int count) = ChunkOffsets(index).Single();
        Assert.True(count >= 2, $"{count} chunk: nothing to split between");

        int middle = count / 2;
        int cut = (int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(entries + (middle * 4)));
        for (int i = middle; i < count; i++)
        {
            int at = entries + (i * 4);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(at), BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(at)) + 8);
        }

        index.CopyTo(file, moov.Start);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(mdat.Start), (uint)(cut - mdat.Start));
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)((mdat.End - cut) + 8));
        "mdat"u8.CopyTo(header.AsSpan(4));

        using FileStream output = File.Create(path);
        output.Write(file, 0, cut);
        output.Write(header);
        output.Write(file, cut, file.Length - cut);
    }

    private readonly record struct TopAtom(string Type, int Start, int Size)
    {
        public int End => Start + Size;
    }

    private static List<TopAtom> TopLevel(byte[] file)
    {
        var atoms = new List<TopAtom>();
        for (int at = 0; at + 8 <= file.Length;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            atoms.Add(new TopAtom(System.Text.Encoding.ASCII.GetString(file, at + 4, 4), at, size));
            at += size;
        }

        return atoms;
    }

    // Each stco table's entries and count: its bytes are the tag, 4 of version and flags, the count, the offsets.
    private static IEnumerable<(int Entries, int Count)> ChunkOffsets(byte[] index)
    {
        for (int at = 0; at + 12 <= index.Length; at++)
        {
            if (index[at] == 's' && index[at + 1] == 't' && index[at + 2] == 'c' && index[at + 3] == 'o')
            {
                yield return (at + 12, (int)BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(at + 8)));
            }
        }
    }

    private sealed class OpenedFile : IDisposable
    {
        private AVFormatContext* _format;

        private OpenedFile(AVFormatContext* format) => _format = format;

        public AVFormatContext* Format => _format;

        public static OpenedFile ThroughInput(string path)
        {
            FFmpegRuntime.EnsureInitialized();
            AVFormatContext* format = null;
            Assert.True(LibavInput.Open(&format, path) >= 0);
            Assert.True(ffmpeg.avformat_find_stream_info(format, null) >= 0);
            return new OpenedFile(format);
        }

        public static OpenedFile Plain(string path)
        {
            FFmpegRuntime.EnsureInitialized();
            AVFormatContext* format = null;
            Assert.True(ffmpeg.avformat_open_input(&format, path, null, null) >= 0);
            Assert.True(ffmpeg.avformat_find_stream_info(format, null) >= 0);
            return new OpenedFile(format);
        }

        public List<(long Pts, int Size, long Sum)> ReadPacketsFrom(double fraction)
        {
            Assert.True(ffmpeg.av_seek_frame(_format, -1, (long)(_format->duration * fraction), ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0);
            return ReadPackets(12);
        }

        public List<(long Pts, int Size, long Sum)> ReadPackets(int most)
        {
            var packets = new List<(long, int, long)>();
            AVPacket* packet = ffmpeg.av_packet_alloc();
            while (packets.Count < most && ffmpeg.av_read_frame(_format, packet) >= 0)
            {
                long sum = 0;
                for (int i = 0; i < packet->size; i++)
                {
                    sum = (sum * 31) + packet->data[i];
                }

                packets.Add((packet->pts, packet->size, sum));
                ffmpeg.av_packet_unref(packet);
            }

            ffmpeg.av_packet_free(&packet);
            return packets;
        }

        public void Dispose()
        {
            AVFormatContext* format = _format;
            if (format != null)
            {
                ffmpeg.avformat_close_input(&format);
                _format = null;
            }
        }
    }
}
