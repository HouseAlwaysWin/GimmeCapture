using System;
using System.Collections.Generic;
using System.Diagnostics;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Windows;
using Moq;
using Xunit;

namespace GimmeCapture.Tests;

public class HotkeyServiceTests
{
    private class TestableHotkeyService : WindowsGlobalHotkeyService
    {
        public bool LastRegResult = true;
        public int RegCallCount = 0;
        public int UnregCallCount = 0;
        public List<(int id, uint mods, uint vkey)> RegHistory = new();

        // Presses reach the "UI thread" through postToUi; by default they run at once.
        public TestableHotkeyService(Action<Action>? postToUi = null)
            : base(postToUi ?? (action => action()))
        {
        }

        public void SetHandle(IntPtr handle)
        {
            var field = typeof(WindowsGlobalHotkeyService).GetField("_handle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(this, handle);
        }

        protected override bool NativeRegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vkey)
        {
            RegCallCount++;
            RegHistory.Add((id, fsModifiers, vkey));
            return LastRegResult;
        }

        protected override bool NativeUnregisterHotKey(IntPtr hWnd, int id)
        {
            UnregCallCount++;
            return true;
        }

        // Processes running at a higher integrity level than this one, as the foreground monitor would find them.
        public HashSet<uint> ElevatedPids { get; } = new();

        protected override bool IsProcessHigherIntegrity(uint pid) => ElevatedPids.Contains(pid);
    }

    [Fact]
    public void Register_WhenNotSuspended_CallsNativeRegister()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));

        // Act
        service.Register(1, "F1");

        // Assert
        Assert.Equal(1, service.RegCallCount);
        Assert.Equal(1, service.RegHistory[0].id);
        Assert.Equal(0x70u, service.RegHistory[0].vkey); // F1 is 0x70
    }

    [Fact]
    public void Register_WithShiftModifier_RegistersShiftFunctionKey()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));

        // Act
        service.Register(3, "Shift+F1");

        // Assert
        Assert.Equal(1, service.RegCallCount);
        Assert.Equal(3, service.RegHistory[0].id);
        Assert.Equal(0x0004u, service.RegHistory[0].mods); // MOD_SHIFT
        Assert.Equal(0x70u, service.RegHistory[0].vkey);   // F1
    }

    [Fact]
    public void Register_WithShiftEnter_RegistersShiftAndEnter()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));

        // Act
        service.Register(4, "Shift+Enter");

        // Assert
        Assert.Equal(1, service.RegCallCount);
        Assert.Equal(4, service.RegHistory[0].id);
        Assert.Equal(0x0004u, service.RegHistory[0].mods); // MOD_SHIFT
        Assert.Equal(0x0Du, service.RegHistory[0].vkey);   // Enter
    }

    [Theory]
    [InlineData("PrintScreen", 0u)]
    [InlineData("Print", 0u)]
    [InlineData("Ctrl+PrintScreen", 0x0002u)]
    [InlineData("Shift+PrintScreen", 0x0004u)]
    public void Register_WithPrintScreenHotkeys_RegistersSnapshotVirtualKey(string hotkey, uint expectedMods)
    {
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));

        service.Register(11, hotkey);

        Assert.Equal(1, service.RegCallCount);
        Assert.Equal(11, service.RegHistory[0].id);
        Assert.Equal(expectedMods, service.RegHistory[0].mods);
        Assert.Equal(0x2Cu, service.RegHistory[0].vkey);
    }

    [Theory]
    [InlineData("F6", 0x75u)]
    [InlineData("F7", 0x76u)]
    [InlineData("F8", 0x77u)]
    [InlineData("F9", 0x78u)]
    public void Register_WithActionFunctionKeys_RegistersExpectedVirtualKeys(string hotkey, uint expectedVKey)
    {
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));

        service.Register(10, hotkey);

        Assert.Equal(1, service.RegCallCount);
        Assert.Equal(10, service.RegHistory[0].id);
        Assert.Equal(0u, service.RegHistory[0].mods);
        Assert.Equal(expectedVKey, service.RegHistory[0].vkey);
    }

    [Fact]
    public void Register_WhenNativeRegistrationFails_RaisesFailureCallback()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.LastRegResult = false;

        int? failedId = null;
        string? failedHotkey = null;
        int? failedError = null;
        service.OnHotkeyRegistrationFailed = (id, hotkey, error) =>
        {
            failedId = id;
            failedHotkey = hotkey;
            failedError = error;
        };

        // Act
        service.Register(9, "Shift+F1");

        // Assert
        Assert.Equal(9, failedId);
        Assert.Equal("Shift+F1", failedHotkey);
        Assert.True(failedError.HasValue);
    }

    [Fact]
    public void Register_WhenSuspended_DoesNotCallNativeRegister()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.SuspendAll();
        service.RegCallCount = 0; // Reset count from SuspendAll unreg doesn't affect reg count

        // Act
        service.Register(2, "F2");

        // Assert
        Assert.Equal(0, service.RegCallCount);
    }

    [Fact]
    public void ResumeAll_ShouldRegisterLatestValue_NotSuspendedValue()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        
        // Initial state: F1 registered
        service.Register(100, "F1");
        Assert.Equal(1, service.RegCallCount);

        // Suspend
        service.SuspendAll();
        
        // While suspended, change F1 to F4
        service.Register(100, "F4");
        
        Assert.Equal(1, service.RegCallCount); // Still 1 from initial

        // Act
        service.ResumeAll();

        // Assert
        // Should have called register for F4
        Assert.Equal(2, service.RegCallCount);
        Assert.Equal(100, service.RegHistory[1].id);
        Assert.Equal(0x73u, service.RegHistory[1].vkey); // F4 is 0x73
    }

    [Fact]
    public void SuspendAll_UnregistersActiveHotkeys()
    {
        // Arrange
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.Register(1, "F1");
        service.Register(2, "F2");
        
        int unregBefore = service.UnregCallCount;

        // Act
        service.SuspendAll();

        // Assert
        // Should unregister 1 and 2
        Assert.Equal(unregBefore + 2, service.UnregCallCount);
    }

    [Fact]
    public void AnAdministratorWindow_RaisesTheNotice_ForHotkeysRegisterHotKeyAccepted()
    {
        // Windows withholds WM_HOTKEY from us under an administrator window as it does the hook. The notice used to
        // assume WM_HOTKEY got through and spoke up only for hook-only hotkeys, so Shift+F1 just did nothing there.
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.Register(HotkeyIds.Snip, "Shift+F1");
        service.ElevatedPids.Add(4242);

        Assert.True(service.ShouldRaiseElevatedNotice(4242, nowMs: 10_000));
    }

    [Fact]
    public void AnAdministratorWindow_RaisesTheNotice_OncePerProcess()
    {
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.Register(HotkeyIds.Snip, "Shift+F1");
        service.ElevatedPids.Add(4242);
        service.ElevatedPids.Add(4343);

        Assert.True(service.ShouldRaiseElevatedNotice(4242, nowMs: 10_000));
        Assert.False(service.ShouldRaiseElevatedNotice(4242, nowMs: 20_000));
        Assert.True(service.ShouldRaiseElevatedNotice(4343, nowMs: 30_000));
    }

    [Fact]
    public void AnotherAdministratorWindow_WithinASecondAndAHalf_WaitsItsTurn()
    {
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.Register(HotkeyIds.Snip, "Shift+F1");
        service.ElevatedPids.Add(4242);
        service.ElevatedPids.Add(4343);

        Assert.True(service.ShouldRaiseElevatedNotice(4242, nowMs: 10_000));
        Assert.False(service.ShouldRaiseElevatedNotice(4343, nowMs: 10_500));
        Assert.True(service.ShouldRaiseElevatedNotice(4343, nowMs: 12_000));
    }

    [Fact]
    public void NoNotice_WithoutHotkeys_OrForAWindowAtOurLevel()
    {
        var service = new TestableHotkeyService();
        service.SetHandle(new IntPtr(1234));
        service.ElevatedPids.Add(4242);

        Assert.False(service.ShouldRaiseElevatedNotice(4242, nowMs: 10_000));

        service.Register(HotkeyIds.Snip, "Shift+F1");
        Assert.False(service.ShouldRaiseElevatedNotice(5555, nowMs: 20_000));
        Assert.True(service.ShouldRaiseElevatedNotice(4242, nowMs: 30_000));
    }

    private const int VkF1 = 0x70;

    [Fact]
    public void AHotkeyPress_IsSwallowed_AndHandedToTheUiThreadWithItsPressTime()
    {
        // The hook gives the key back to Windows at once; the press itself runs later, on the UI thread.
        var posted = new List<Action>();
        var service = new TestableHotkeyService(posted.Add);
        service.SetHandle(new IntPtr(1234));
        service.Register(1, "F1");
        HotkeyPress? received = null;
        service.OnHotkeyPressed = press => received = press;

        long before = Stopwatch.GetTimestamp();
        bool swallowed = service.ProcessKeyEventForTest(VkF1, isKeyDown: true, isKeyUp: false);

        Assert.True(swallowed);
        Assert.Null(received);
        Assert.Single(posted)();
        Assert.Equal(1, received?.Id);
        Assert.InRange(received!.Value.PressedAtTimestamp, before, Stopwatch.GetTimestamp());
    }

    [Fact]
    public void WhileSuspended_TheHookLetsAnExistingHotkeyThrough_AndMatchesAgainAfterResume()
    {
        // The hotkeys page suspends every hotkey while it records a new combo. The hook kept matching them anyway, so
        // pressing an existing hotkey there fired it and swallowed the key the page was trying to record.
        var posted = new List<Action>();
        var service = new TestableHotkeyService(posted.Add);
        service.SetHandle(new IntPtr(1234));
        service.Register(1, "F1");

        service.SuspendAll();
        Assert.False(service.ProcessKeyEventForTest(VkF1, isKeyDown: true, isKeyUp: false));
        service.ProcessKeyEventForTest(VkF1, isKeyDown: false, isKeyUp: true);
        Assert.Empty(posted);

        service.ResumeAll();
        Assert.True(service.ProcessKeyEventForTest(VkF1, isKeyDown: true, isKeyUp: false));
        Assert.Single(posted);
    }
}
