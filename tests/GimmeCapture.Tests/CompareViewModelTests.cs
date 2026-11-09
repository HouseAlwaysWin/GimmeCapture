using System.Threading.Tasks;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Unit-level checks for the compare view model's framing/timeline math + safe behavior before a sample window is
// encoded. The scrubber now spans the whole clip; the compressed side samples a short window on demand. Actual
// decode/playback is covered by the gated real-encode integration tests.
public class CompareViewModelTests
{
    private static CompareViewModel Make(double duration = 100, int fps = 30, int width = 720, int height = 404, int rotation = 0)
        => new(new LibavStillFrameDecoder(@"C:\does-not-exist.mp4", width, height), duration, fps, new LibavExportOptions(), rotation);

    [Fact]
    public void Constructor_SpansFullDuration_AndSetsInitialState()
    {
        using var vm = Make(duration: 100);

        Assert.Equal(100, vm.WindowLength);            // slider spans the whole clip
        Assert.Equal("0:00 / 1:40", vm.TimeText);
        Assert.Equal("▶", vm.PlayPauseGlyph);
        Assert.True(vm.IsPreparing);                   // compressed side samples before the first window is ready
        Assert.False(vm.IsPlaying);
        Assert.Equal(0, vm.PositionSeconds);
    }

    [Fact]
    public void WindowLength_IsFullDuration_ForShortClip()
    {
        using var vm = Make(duration: 6);

        Assert.Equal(6, vm.WindowLength);
        Assert.Equal("0:00 / 0:06", vm.TimeText);
    }

    [Fact]
    public async Task SeekAndScrub_BeforeSampleReady_DoNotThrow()
    {
        using var vm = Make(duration: 100);

        await vm.SeekAsync(5);   // no sample dir yet -> source decode guarded, no window encoded
        await vm.SeekAsync(-3);  // clamps without throwing
        vm.BeginScrub();

        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void WindowStartAround_StartsSlightlyBeforePos()
    {
        // The window starts a little before the requested position (winLen*0.25 = 1s for a 4s window).
        Assert.Equal(39, CompareSampleWindows.WindowStartAround(100, 4, 40));
    }

    [Fact]
    public void WindowStartAround_ClampsToZeroNearStart()
    {
        Assert.Equal(0, CompareSampleWindows.WindowStartAround(100, 4, 0.5));
    }

    [Fact]
    public void WindowStartAround_ClampsSoWindowFitsNearEnd()
    {
        // Near the end the window slides back so the whole 4s window still fits inside the 100s clip.
        Assert.Equal(96, CompareSampleWindows.WindowStartAround(100, 4, 100));
    }

    [Fact]
    public void WindowStartAround_ShortClip_AlwaysZero()
    {
        // When the window is the whole (short) clip, the start stays pinned at 0 regardless of position.
        Assert.Equal(0, CompareSampleWindows.WindowStartAround(6, 6, 3));
    }
}
