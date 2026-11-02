using System.Runtime.InteropServices;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

// What a UiStall line reads to tell a UI thread waiting on pages from one waiting on a lock.
public sealed class ProcessPageFaultsTests
{
    [WindowsFact]
    public unsafe void Read_CountsThePagesTheProcessFaultsIn()
    {
        uint before = ProcessPageFaults.Read() ?? throw new InvalidOperationException("no page fault count");

        // Fresh memory is faulted in a page at a time on first touch.
        const int Pages = 4096;
        byte* memory = (byte*)NativeMemory.Alloc(Pages * 4096);
        try
        {
            for (int page = 0; page < Pages; page++)
            {
                memory[page * 4096] = 1;
            }

            uint after = ProcessPageFaults.Read() ?? throw new InvalidOperationException("no page fault count");
            Assert.InRange(after - before, (uint)(Pages / 2), uint.MaxValue);
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }
}
