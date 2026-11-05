using System.Diagnostics;
using System.Runtime.InteropServices;
using GimmeCapture.Launcher;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

// The launcher read the files a start maps whole at sign-in, 141 MB in 22.6 s from a busy hard disk, and the start used
// less than half: the app now writes down the stretches of each file the start used, from its working set.
public sealed class ImagePagesInUseTests
{
    [Fact]
    public void ALayoutFromAPeImagesHeaders_PutsEachSectionInsideTheFile()
    {
        string file = typeof(ImagePagesInUseTests).Assembly.Location;
        var headers = new byte[4096];
        using (var stream = File.OpenRead(file))
        {
            stream.ReadExactly(headers, 0, (int)Math.Min(headers.Length, stream.Length));
        }

        var layout = ImageLayout.Parse(headers);

        Assert.NotNull(layout);
        Assert.InRange(layout.SizeOfHeaders, 0x200, 0x1000);
        Assert.NotEmpty(layout.Sections);
        long length = new FileInfo(file).Length;
        Assert.All(layout.Sections, section =>
        {
            Assert.True(section.VirtualAddress >= layout.SizeOfHeaders, $"Section at {section.VirtualAddress} overlaps the headers.");
            Assert.InRange(section.PointerToRawData + section.SizeOfRawData, 0, length);
        });
    }

    [Fact]
    public void ALayout_IsOnlyReadFromAPeImage()
    {
        Assert.Null(ImageLayout.Parse(new byte[4096]));
        var notPe = new byte[4096];
        notPe[0] = (byte)'M';
        notPe[1] = (byte)'Z';
        notPe[0x3C] = 0x80;
        Assert.Null(ImageLayout.Parse(notPe));
        // Its header claimed past the end of what there is.
        notPe[0x3C] = 0xF0;
        notPe[0x3D] = 0x0F;
        Assert.Null(ImageLayout.Parse(notPe));
    }

    [Fact]
    public void PagesInUse_AreTheirSectionsDataInTheFile_JoinedWhereClose()
    {
        // Headers in the first 0x400 bytes of the file; code at 0x1000 in memory from 0x400 in the file, 0x2400 bytes of
        // it; data at 0x4000 from 0x2800, 0x200 bytes, the rest of it zeroes in memory only.
        var layout = new ImageLayout(0x400,
        [
            new ImageSection(VirtualAddress: 0x1000, SizeOfRawData: 0x2400, PointerToRawData: 0x400),
            new ImageSection(VirtualAddress: 0x4000, SizeOfRawData: 0x200, PointerToRawData: 0x2800),
        ]);
        long[] pages = [0x5000, 0x0, 0x1000, 0x3000, 0x4000];

        Assert.Equal(
            [new FileRange(0, 0x1400), new FileRange(0x2400, 0x600)],
            ImagePagesInUse.ToFileRanges(pages, layout, joinGap: 0));
        Assert.Equal([new FileRange(0, 0x2A00)], ImagePagesInUse.ToFileRanges(pages, layout, joinGap: 0x1000));
        Assert.Empty(ImagePagesInUse.ToFileRanges([0x5000], layout, joinGap: 0x1000));
    }

    [WindowsFact]
    public void ANativeLibraryLoadedHere_HasItsHeadersInUse_AndNotAllOfItsFile()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "libSkiaSharp.dll");
        NativeLibrary.Load(file);
        using var process = Process.GetCurrentProcess();
        var module = process.Modules.Cast<ProcessModule>()
            .Single(m => string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase));

        var inUse = ImagePagesInUse.InUse(module.BaseAddress, module.ModuleMemorySize);

        Assert.NotNull(inUse);
        long length = new FileInfo(file).Length;
        Assert.Equal(0, inUse[0].Offset);
        Assert.All(inUse, range => Assert.InRange(range.End, 1, length));
        Assert.True(inUse.Sum(range => range.Length) < length, $"All {length} bytes in use after loading it.");

        // The layout's arithmetic puts a section's pages where the file has them.
        var headers = new byte[4096];
        Marshal.Copy(module.BaseAddress, headers, 0, headers.Length);
        var section = ImageLayout.Parse(headers)!.Sections.Last(s => s.SizeOfRawData >= 512);
        var inMemory = new byte[512];
        Marshal.Copy(module.BaseAddress + (nint)section.VirtualAddress, inMemory, 0, inMemory.Length);
        var inFile = new byte[512];
        using var stream = File.OpenRead(file);
        stream.Position = section.PointerToRawData;
        stream.ReadExactly(inFile);
        Assert.Equal(inFile, inMemory);
    }
}
