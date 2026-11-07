using GimmeCapture.Services.Platforms.Desktop;

namespace GimmeCapture.Tests;

/// <summary>
/// The capture overlay's window is built ahead, while the app is idle, so a capture skips its build (25-33 ms in
/// OverlayPrepareProbeTests, 32-57 in the user's log) for 2.9-3.4 MB held while it waits
/// (<see cref="PreparedWindow{T}"/>).
/// </summary>
public class PreparedWindowTests
{
    private sealed class Clock
    {
        public List<(Action Action, TimeSpan Delay)> Due { get; } = [];

        public void Schedule(Action action, TimeSpan delay) => Due.Add((action, delay));

        public void RunDue()
        {
            var due = Due.ToList();
            Due.Clear();
            foreach (var (action, _) in due)
            {
                action();
            }
        }
    }

    private static (PreparedWindow<object> Prepared, Clock Clock, List<object> Built) Create(
        Func<bool>? overlayOpen = null)
    {
        var clock = new Clock();
        var built = new List<object>();
        var prepared = new PreparedWindow<object>(
            () =>
            {
                var window = new object();
                built.Add(window);
                return window;
            },
            overlayOpen ?? (() => false),
            clock.Schedule,
            TimeSpan.FromSeconds(3));
        return (prepared, clock, built);
    }

    [Fact]
    public void AWindowIsBuiltAfterTheDelay_AndTakenOnce()
    {
        var (prepared, clock, built) = Create();
        Assert.Null(prepared.Take());

        prepared.PrepareSoon();
        Assert.Empty(built);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(clock.Due).Delay);

        clock.RunDue();
        Assert.True(prepared.IsReady);
        Assert.Same(built.Single(), prepared.Take());
        Assert.Null(prepared.Take());
    }

    [Fact]
    public void AskingAgain_WhileOneIsDueOrWaiting_BuildsNoSecond()
    {
        var (prepared, clock, built) = Create();

        prepared.PrepareSoon();
        prepared.PrepareSoon();
        Assert.Single(clock.Due);
        clock.RunDue();

        prepared.PrepareSoon();
        Assert.Empty(clock.Due);
        Assert.Single(built);
    }

    [Fact]
    public void NothingIsBuiltWhileAnOverlayIsUp_AndTheNextAskTriesAgain()
    {
        bool overlayOpen = true;
        var (prepared, clock, built) = Create(() => overlayOpen);

        prepared.PrepareSoon();
        clock.RunDue();
        Assert.Empty(built);
        Assert.False(prepared.IsReady);

        overlayOpen = false;
        prepared.PrepareSoon();
        clock.RunDue();
        Assert.True(prepared.IsReady);
    }

    [Fact]
    public void ABuildThatFails_LeavesTheCaptureToBuildItsOwn_AndIsTriedAgainLater()
    {
        var clock = new Clock();
        int attempts = 0;
        var prepared = new PreparedWindow<object>(
            () => ++attempts == 1 ? throw new InvalidOperationException("no window") : new object(),
            () => false,
            clock.Schedule,
            TimeSpan.FromSeconds(3));

        prepared.PrepareSoon();
        clock.RunDue();
        Assert.Null(prepared.Take());

        prepared.PrepareSoon();
        clock.RunDue();
        Assert.NotNull(prepared.Take());
    }
}
