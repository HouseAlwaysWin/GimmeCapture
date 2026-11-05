using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using GimmeCapture.Launcher;

namespace GimmeCapture.Services.Interop;

/// <summary>
/// Which stretches of an image file this process has used: the pages of the file's mapping in the working set, turned
/// into stretches of the file through the image's section table, those close together joined. The installed app, its
/// window opened and a few captures taken, held 56 of the 152 MB its folder's files map, 6 of its own assembly's 39.8 MB
/// (30 of them the CJK fonts), while the launcher read the files whole at sign-in, 141 MB in 22.6 s. Windows only;
/// elsewhere there is nothing to tell.
/// </summary>
internal static class ImagePagesInUse
{
    internal const int PageSize = 4096;

    /// <summary>
    /// Stretches of a file less than this apart are read as one: on a hard disk a seek costs as long as reading about a
    /// megabyte, and a start's pages of code lie scattered a few kilobytes apart.
    /// </summary>
    internal const long JoinGap = 256 * 1024;

    // The working set is asked about this many pages a call.
    private const int Batch = 4096;

    /// <summary>
    /// The stretches of the file mapped as an image at <paramref name="imageBase"/> whose pages are in this process's
    /// working set; null when it cannot tell, or when no page is, as when the working set has just been emptied.
    /// </summary>
    internal static List<FileRange>? InUse(IntPtr imageBase, int imageSize)
    {
        if (!OperatingSystem.IsWindows() || imageBase == IntPtr.Zero || imageSize <= 0)
        {
            return null;
        }

        var headers = new byte[Math.Min(PageSize, imageSize)];
        Marshal.Copy(imageBase, headers, 0, headers.Length);
        if (ImageLayout.Parse(headers) is not { } layout || ResidentPages(imageBase, imageSize) is not { Count: > 0 } pages)
        {
            return null;
        }

        return ToFileRanges(pages, layout, JoinGap);
    }

    /// <summary>
    /// The file's stretches behind the pages at <paramref name="pageOffsets"/> (from the image's start), sorted and
    /// joined where less than <paramref name="joinGap"/> apart. A page beyond a section's data in the file, zeroes in
    /// memory only, has none.
    /// </summary>
    internal static List<FileRange> ToFileRanges(IEnumerable<long> pageOffsets, ImageLayout layout, long joinGap)
    {
        var pieces = new List<(long Start, long End)>();
        foreach (long page in pageOffsets)
        {
            long pageEnd = page + PageSize;
            if (page < layout.SizeOfHeaders)
            {
                pieces.Add((page, Math.Min(pageEnd, layout.SizeOfHeaders)));
            }

            foreach (var section in layout.Sections)
            {
                long start = Math.Max(page, section.VirtualAddress);
                long end = Math.Min(pageEnd, section.VirtualAddress + section.SizeOfRawData);
                if (start < end)
                {
                    long offset = section.PointerToRawData - section.VirtualAddress;
                    pieces.Add((start + offset, end + offset));
                }
            }
        }

        pieces.Sort();
        var ranges = new List<FileRange>();
        long runStart = -1, runEnd = -1;
        foreach (var (start, end) in pieces)
        {
            if (runStart >= 0 && start <= runEnd + joinGap)
            {
                runEnd = Math.Max(runEnd, end);
                continue;
            }

            if (runStart >= 0)
            {
                ranges.Add(new FileRange(runStart, runEnd - runStart));
            }

            (runStart, runEnd) = (start, end);
        }

        if (runStart >= 0)
        {
            ranges.Add(new FileRange(runStart, runEnd - runStart));
        }

        return ranges;
    }

    // The offsets from the image's start of its pages in this process's working set; null when Windows will not say.
    private static List<long>? ResidentPages(IntPtr imageBase, int imageSize)
    {
        long pages = ((long)imageSize + PageSize - 1) / PageSize;
        var resident = new List<long>();
        var info = new WorkingSetExInformation[Batch];
        IntPtr process = Kernel32.GetCurrentProcess();
        for (long first = 0; first < pages; first += Batch)
        {
            int count = (int)Math.Min(Batch, pages - first);
            for (int i = 0; i < count; i++)
            {
                info[i] = new WorkingSetExInformation { VirtualAddress = imageBase + (nint)((first + i) * PageSize) };
            }

            if (!QueryWorkingSetEx(process, info, count * Marshal.SizeOf<WorkingSetExInformation>()))
            {
                return null;
            }

            for (int i = 0; i < count; i++)
            {
                // Bit 0 of the attributes: the page is valid, in the working set.
                if ((info[i].Attributes & 1) != 0)
                {
                    resident.Add((first + i) * PageSize);
                }
            }
        }

        return resident;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetExInformation
    {
        public IntPtr VirtualAddress;
        public nint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32QueryWorkingSetEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSetEx(IntPtr process, [In, Out] WorkingSetExInformation[] info, int size);
}

/// <summary>Where an image's headers and sections lie in its file, from its PE headers.</summary>
internal sealed class ImageLayout
{
    internal ImageLayout(long sizeOfHeaders, IReadOnlyList<ImageSection> sections)
    {
        SizeOfHeaders = sizeOfHeaders;
        Sections = sections;
    }

    internal long SizeOfHeaders { get; }

    internal IReadOnlyList<ImageSection> Sections { get; }

    /// <summary>The layout the headers at the start of a PE image give; null for anything else.</summary>
    internal static ImageLayout? Parse(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            return null;
        }

        int nt = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(0x3C));
        // The signature, the 20-byte file header and the optional header up to SizeOfHeaders, at 60.
        if (nt < 0x40 || nt > image.Length - (4 + 20 + 64) || BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(nt)) != 0x00004550)
        {
            return null;
        }

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(nt + 6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(nt + 20));
        int optional = nt + 24;
        uint sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(optional + 60));
        int table = optional + optionalSize;
        if (table < optional + 64 || table > image.Length - (sectionCount * 40))
        {
            return null;
        }

        var sections = new List<ImageSection>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            var header = image.Slice(table + (i * 40), 40);
            sections.Add(new ImageSection(
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(12)),
                SizeOfRawData: BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16)),
                PointerToRawData: BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20))));
        }

        return new ImageLayout(sizeOfHeaders, sections);
    }
}

/// <summary>A section of a PE image: where it lies in memory, from the image's start, and its data in the file.</summary>
internal readonly record struct ImageSection(long VirtualAddress, long SizeOfRawData, long PointerToRawData);
