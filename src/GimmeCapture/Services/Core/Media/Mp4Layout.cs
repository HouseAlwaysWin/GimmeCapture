using System;
using System.Buffers.Binary;
using System.IO;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Reads how an MP4 file is laid out at its top level, from the first few atoms alone. libav's reader, once it has the
/// index (<c>moov</c>) and the start of the media data (<c>mdat</c>), stops only if that data runs to the end of the file;
/// otherwise it reads the header of every remaining top-level atom, looking for fragments. Most files have one <c>mdat</c>,
/// but some tools write one for every chunk: 78 videos of the user's on a hard disk included four with 1,468-3,162 of
/// them, and each took 36-46 s just to open, every time.
/// </summary>
internal static class Mp4Layout
{
    private const uint Ftyp = 0x66747970;
    private const uint Moov = 0x6D6F6F76;
    private const uint Mvex = 0x6D766578;
    private const uint Mdat = 0x6D646174;
    private const uint Free = 0x66726565;
    private const uint Skip = 0x736B6970;
    private const uint Wide = 0x77696465;

    private const int MaxAtomsBeforeMediaData = 16;
    private const int MaxIndexChildren = 256;

    /// <summary>
    /// Whether the file has its whole index first and then its media data in more than one top-level atom: not
    /// fragmented (an index with <c>mvex</c> leaves the samples to the fragments, which do have to be read). False for
    /// anything else, or when the file cannot be read, which libav's own open then reports.
    /// </summary>
    internal static bool HasIndexThenSplitMediaData(string path)
    {
        try
        {
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.RandomAccess);
            return HasIndexThenSplitMediaData(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool HasIndexThenSplitMediaData(Stream file)
    {
        long length = file.Length;
        long position = 0;
        bool sawType = false;
        bool sawIndex = false;
        for (int atoms = 0; atoms < MaxAtomsBeforeMediaData; atoms++)
        {
            if (!TryReadAtom(file, position, length, out Atom atom))
            {
                return false;
            }

            switch (atom.Type)
            {
                case Ftyp:
                    sawType = true;
                    break;
                case Free or Skip or Wide:
                    break;
                case Moov:
                    if (!sawType || sawIndex || HasChild(file, atom, Mvex))
                    {
                        return false;
                    }

                    sawIndex = true;
                    break;
                case Mdat:
                    return sawIndex && TryReadAtom(file, atom.End, length, out Atom next) && next.Type == Mdat;
                default:
                    return false;
            }

            position = atom.End;
        }

        return false;
    }

    private static bool HasChild(Stream file, Atom parent, uint type)
    {
        long position = parent.ContentStart;
        for (int children = 0; children < MaxIndexChildren && position < parent.End; children++)
        {
            if (!TryReadAtom(file, position, parent.End, out Atom child))
            {
                return false;
            }

            if (child.Type == type)
            {
                return true;
            }

            position = child.End;
        }

        return false;
    }

    // The atom at position, inside limit. False where there is none, or one that says it runs past limit or is sized
    // "to the end of the file".
    private static bool TryReadAtom(Stream file, long position, long limit, out Atom atom)
    {
        atom = default;
        if (position < 0 || limit - position < 8)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[16];
        file.Seek(position, SeekOrigin.Begin);
        int read = file.ReadAtLeast(header, 8, throwOnEndOfStream: false);
        if (read < 8)
        {
            return false;
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        int headerSize = 8;
        if (size == 1)
        {
            if (read < 16)
            {
                return false;
            }

            size = BinaryPrimitives.ReadInt64BigEndian(header[8..]);
            headerSize = 16;
        }

        if (size < headerSize || size > limit - position)
        {
            return false;
        }

        atom = new Atom(BinaryPrimitives.ReadUInt32BigEndian(header[4..]), position + headerSize, position + size);
        return true;
    }

    private readonly record struct Atom(uint Type, long ContentStart, long End);
}
