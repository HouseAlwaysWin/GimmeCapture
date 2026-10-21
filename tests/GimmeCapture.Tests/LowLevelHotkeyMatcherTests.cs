using System.Collections.Generic;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

public class LowLevelHotkeyMatcherTests
{
    private const int F1 = 0x70;
    private const int LeftCtrl = 0xA2;
    private const int Shift = 0x10;

    private readonly LowLevelHotkeyMatcher _matcher = new();
    private readonly Dictionary<int, (uint Mods, uint Key)> _registered = new();
    private uint _otherHeldModifiers;
    private long _nowMs = 10_000;

    private int? Down(int vk, bool suspended = false) =>
        _matcher.OnKey(vk, isKeyDown: true, isKeyUp: false, () => _otherHeldModifiers, _registered, suspended, _nowMs);

    private int? Up(int vk) =>
        _matcher.OnKey(vk, isKeyDown: false, isKeyUp: true, () => _otherHeldModifiers, _registered, suspended: false, _nowMs);

    [Fact]
    public void AKeyWithExactlyTheRegisteredModifiers_FiresThatHotkey()
    {
        _registered[7] = (LowLevelHotkeyMatcher.ModControl, F1);

        Assert.Null(Down(LeftCtrl));
        Assert.Equal(7, Down(F1));
    }

    [Fact]
    public void AMissingOrExtraModifier_DoesNotMatch()
    {
        _registered[7] = (LowLevelHotkeyMatcher.ModControl, F1);

        Assert.Null(Down(F1));
        Up(F1);
        Down(LeftCtrl);
        Down(Shift);
        Assert.Null(Down(F1));
    }

    [Fact]
    public void AModifierTheHookNeverSawGoDown_StillCounts()
    {
        _registered[7] = (LowLevelHotkeyMatcher.ModControl, F1);
        _otherHeldModifiers = LowLevelHotkeyMatcher.ModControl;

        Assert.Equal(7, Down(F1));
    }

    [Fact]
    public void KeyUpsAndModifierKeys_NeverMatch()
    {
        _registered[7] = (0, F1);

        Assert.Null(Up(F1));
        Assert.Null(Down(LeftCtrl));
        Assert.Null(Down(Shift));
    }

    [Fact]
    public void AHeldKey_FiresOnce_UntilItIsReleased()
    {
        _registered[7] = (0, F1);

        Assert.Equal(7, Down(F1));
        _nowMs += 500;
        Assert.Null(Down(F1)); // auto-repeat: no key-up in between
        Up(F1);
        Assert.Equal(7, Down(F1));
    }

    [Fact]
    public void TheSamePressFromASecondSource_IsDebounced()
    {
        _registered[7] = (0, F1);

        Assert.Equal(7, Down(F1));
        Assert.False(_matcher.TryFire(7, _nowMs + 50)); // WM_HOTKEY for the press the hook already handled
        Up(F1);
        _nowMs += 100;
        Assert.Null(Down(F1));
        Up(F1);
        _nowMs += LowLevelHotkeyMatcher.DebounceMs;
        Assert.Equal(7, Down(F1));
    }

    [Fact]
    public void WhileSuspended_NothingMatches()
    {
        // The hotkeys page suspends every hotkey while it records a new combo. The hook kept matching them anyway, so
        // pressing an existing hotkey there fired it and swallowed the key the page was trying to record.
        _registered[7] = (0, F1);

        Assert.Null(Down(F1, suspended: true));
        Up(F1);
        Assert.Equal(7, Down(F1));
    }

    [Fact]
    public void ForgettingAHotkey_ClearsItsDebounce()
    {
        _registered[7] = (0, F1);

        Assert.Equal(7, Down(F1));
        _matcher.Forget(7);
        Assert.True(_matcher.TryFire(7, _nowMs + 1));
    }

    [Fact]
    public void Reset_ForgetsHeldKeysAndFirings()
    {
        _registered[7] = (0, F1);

        Assert.Equal(7, Down(F1));
        _matcher.Reset();
        Assert.Equal(7, Down(F1));
    }
}
