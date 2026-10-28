using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// A file added to the compress queue is probed in the background: its size, frame rate and length come from the same
// open of the file that decodes its row thumbnail (LibavVideoFramePlayer.ProbeWithThumbnailAsync, tested on its own).
// The thumbnail itself needs Avalonia's renderer, which a unit test does not have, so it is not checked here.
public sealed class CompressQueueProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "GimmeCapture.Tests", nameof(CompressQueueProbeTests), Guid.NewGuid().ToString("N"));

    public CompressQueueProbeTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // %TEMP% clean-up gets it later.
        }
    }

    [Fact]
    public async Task AQueuedVideo_GetsItsSizeAndFrameRate_FromItsProbe()
    {
        // The queue takes only video file names; libav goes by what the file holds.
        string clip = WriteClip(Path.Combine(_dir, "clip.mkv"), width: 64, height: 48);
        var vm = new MainWindowViewModel();

        // An item is in the queue before its probe starts. The probe ends by re-raising the item's edit summary.
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.CompressQueue.CollectionChanged += (_, e) =>
        {
            foreach (CompressQueueItem added in e.NewItems?.OfType<CompressQueueItem>() ?? Enumerable.Empty<CompressQueueItem>())
            {
                added.PropertyChanged += (_, changed) =>
                {
                    if (changed.PropertyName == nameof(CompressQueueItem.HasEdits))
                    {
                        probed.TrySetResult();
                    }
                };
            }
        };

        vm.AddPathsToQueue(new[] { clip });
        await probed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        CompressQueueItem item = Assert.Single(vm.CompressQueue);
        Assert.Equal(64, item.ProbedWidth);
        Assert.Equal(48, item.ProbedHeight);
        Assert.Equal(30, item.ProbedFps);
    }

    // A YUV4MPEG2 clip of four flat grey frames at 30 fps.
    private static string WriteClip(string path, int width, int height)
    {
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F30:1 Ip A1:1 C420jpeg\n"));

        var planes = new byte[width * height * 3 / 2];
        planes.AsSpan().Fill(128);
        byte[] frameHeader = Encoding.ASCII.GetBytes("FRAME\n");
        for (int i = 0; i < 4; i++)
        {
            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }
}
