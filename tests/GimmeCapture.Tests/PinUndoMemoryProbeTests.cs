using System.Diagnostics;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.ViewModels.Floating;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What a 4K pin's undo history holds while the pin stays open: the pin rotated 30 times, each rotation keeping the
/// picture before it for undo, with the test process's private bytes read every five, then every step undone. Each step
/// kept 33 MB, 984 MB after 30 (2026-11-04). With the history's pictures capped at 256 MB
/// (<c>AnnotationEditorState.HistoryBitmapBudget</c>), 8 steps and 256-260 MB from the 9th rotation on, undone as far
/// back as they reach. Opt-in (PIN_UNDO_PROBE=1): it reads the whole process, so run it on its own.
/// </summary>
[Collection(AvaloniaRendererCollection.Name)]
public sealed class PinUndoMemoryProbeTests
{
    private const int PictureWidth = 3840;
    private const int PictureHeight = 2160;
    private const int Rotations = 30;

    private readonly ITestOutputHelper _output;

    public PinUndoMemoryProbeTests(ITestOutputHelper output) => _output = output;

    [OptInFact("PIN_UNDO_PROBE=1")]
    public async Task ARotatedPin_KeepsPicturesForUndo()
    {
        var picture = AvaloniaRenderer.Create(PictureWidth, PictureHeight, static (x, y) => 0xFF000000u | ((uint)(x & 0xFF) << 16) | (uint)(y & 0xFF));
        var vm = CreatePin(picture);
        long baseline = PrivateBytes();
        _output.WriteLine($"{PictureWidth}x{PictureHeight} pin, {PictureWidth * PictureHeight * 4 / 1e6:0.0} MB a picture");

        for (int rotation = 1; rotation <= Rotations; rotation++)
        {
            await vm.RotateRightCommand.Execute();
            if (rotation % 5 == 0)
            {
                _output.WriteLine(
                    $"after {rotation,2} rotations: {(PrivateBytes() - baseline) / 1e6,6:0} MB more than the pin alone, " +
                    $"{vm.EditorState.HistoryStack.Count} undo steps");
            }
        }

        int undone = 0;
        while (vm.EditorState.HistoryStack.Count > 0)
        {
            await vm.UndoCommand.Execute();
            undone++;
        }

        _output.WriteLine($"undid {undone} steps, back to {vm.Image!.PixelSize}");
        vm.Dispose();
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static FloatingImageViewModel CreatePin(Bitmap picture)
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.Settings).Returns(new AppSettings());
        var aiPaths = new Mock<AIPathService>(settings.Object);
        var ai = new Mock<AIResourceService>(settings.Object, aiPaths.Object, new Mock<AIModelDownloader>().Object);
        return new FloatingImageViewModel(
            picture, picture.PixelSize.Width, picture.PixelSize.Height, Avalonia.Media.Colors.Red, 2.0, false, false,
            Mock.Of<IClipboardService>(), ai.Object, new SAM2RuntimeService(aiPaths.Object), settings.Object,
            Mock.Of<IBackgroundRemovalService>(), new ResourceQueueService());
    }
}
