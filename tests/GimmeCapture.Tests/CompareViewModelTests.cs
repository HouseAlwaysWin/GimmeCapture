using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Unit-level checks for the compare view model's framing/timeline math + safe behavior before the sample
// is encoded. The actual decode/playback is covered by the gated real-encode integration tests.
public class CompareViewModelTests
{
    private static CompareViewModel Make(double duration = 100, int fps = 30, int width = 1920, int height = 1080, int rotation = 0)
        => new(@"C:\does-not-exist.mp4", duration, fps, width, height, new LibavExportOptions(), rotation);

    [Fact]
    public void Constructor_CapsWindowAndSetsInitialState()
    {
        using var vm = Make(duration: 100);

        Assert.Equal(15, vm.WindowLength);             // ~15s window cap
        Assert.Equal("0:00 / 0:15", vm.TimeText);
        Assert.Equal("▶", vm.PlayPauseGlyph);     // play triangle
        Assert.True(vm.IsPreparing);
        Assert.False(vm.IsPlaying);
        Assert.Equal(0, vm.PositionSeconds);
    }

    [Fact]
    public void Window_UsesWholeClip_WhenShorterThanCap()
    {
        using var vm = Make(duration: 6);

        Assert.Equal(6, vm.WindowLength);
        Assert.Equal("0:00 / 0:06", vm.TimeText);
    }

    [Fact]
    public async Task SeekAndScrub_BeforeSampleReady_DoNotThrow()
    {
        using var vm = Make(duration: 100);

        await vm.SeekAsync(5);   // no sample yet -> pauses, no decode
        await vm.SeekAsync(-3);  // clamps without throwing
        vm.BeginScrub();

        Assert.False(vm.IsPlaying);
    }
}
