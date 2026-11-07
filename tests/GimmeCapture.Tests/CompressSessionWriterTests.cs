using System.Text.Json;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

// Each change of a Compress setting saves the session, at every step of a slider being dragged: the saves go to a
// writer that writes the newest of them off the UI thread, and what is left on Flush, at the process exit.
public sealed class CompressSessionWriterTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "compress-session.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void ASave_WritesNothingOnTheCallersThread_AndFlushWritesTheNewest()
    {
        var writer = new CompressSessionWriter(
            state => CompressSessionService.Write(FilePath, state), TimeSpan.FromMinutes(1));

        writer.Save(new CompressSessionState { OutputFolder = "first", ParallelCount = 1 });
        writer.Save(new CompressSessionState { OutputFolder = "second", ParallelCount = 3 });
        Assert.False(File.Exists(FilePath));

        writer.Flush();

        var written = JsonSerializer.Deserialize<CompressSessionState>(File.ReadAllText(FilePath))!;
        Assert.Equal("second", written.OutputFolder);
        Assert.Equal(3, written.ParallelCount);
    }

    [Fact]
    public async Task ABurstOfSaves_IsOneWrite_OnceTheWaitIsOver()
    {
        int writes = 0;
        CompressSessionState? last = null;
        var writer = new CompressSessionWriter(
            state =>
            {
                Interlocked.Increment(ref writes);
                last = state;
            },
            // Long enough that the burst below, thirteen saves in a loop, is over before it, however busy the machine.
            TimeSpan.FromSeconds(1));

        for (int step = 18; step <= 30; step++)
        {
            writer.Save(new CompressSessionState { ParallelCount = step });
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref writes) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, Volatile.Read(ref writes));
        Assert.Equal(30, last!.ParallelCount);
    }
}
