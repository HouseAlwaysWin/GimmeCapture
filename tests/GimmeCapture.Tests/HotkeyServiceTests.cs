using System;
using System.Collections.Generic;
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
}
