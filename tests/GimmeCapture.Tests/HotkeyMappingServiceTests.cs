using System.Collections.Generic;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// HotkeyMappingService maps a hotkey tag (== a writable string property name on the target VM) to a compiled
// setter. A renamed/removed property makes the tag "unknown" — the setter silently no-ops and ValidateTags flags
// it. These tests pin that contract (the mapping is used to apply saved hotkeys back onto the VM).
public class HotkeyMappingServiceTests
{
    private sealed class FakeHotkeyTarget
    {
        public string SnipHotkey { get; set; } = string.Empty;
        public string RecordHotkey { get; set; } = string.Empty;
        public string ReadOnlyHotkey { get; } = "immutable"; // no setter → not mappable
        public int NotAString { get; set; }                  // wrong type → not mappable
    }

    [Fact]
    public void UpdateViewModelHotkey_SetsMatchingWritableStringProperty()
    {
        var svc = new HotkeyMappingService();
        var target = new FakeHotkeyTarget();

        svc.UpdateViewModelHotkey(target, nameof(FakeHotkeyTarget.SnipHotkey), "Ctrl+Shift+A");

        Assert.Equal("Ctrl+Shift+A", target.SnipHotkey);
        Assert.Equal(string.Empty, target.RecordHotkey); // untouched
    }

    [Fact]
    public void UpdateViewModelHotkey_UnknownTag_IsSilentNoOp()
    {
        var svc = new HotkeyMappingService();
        var target = new FakeHotkeyTarget();

        // A renamed/removed property (or a read-only / non-string one) must not throw and must change nothing.
        svc.UpdateViewModelHotkey(target, "RenamedHotkey", "Ctrl+X");
        svc.UpdateViewModelHotkey(target, nameof(FakeHotkeyTarget.ReadOnlyHotkey), "Ctrl+X");
        svc.UpdateViewModelHotkey(target, nameof(FakeHotkeyTarget.NotAString), "Ctrl+X");

        Assert.Equal(string.Empty, target.SnipHotkey);
        Assert.Equal("immutable", target.ReadOnlyHotkey);
        Assert.Equal(0, target.NotAString);
    }

    [Fact]
    public void UpdateViewModelHotkey_NullOrEmptyInputs_DoNotThrow()
    {
        var svc = new HotkeyMappingService();
        var target = new FakeHotkeyTarget();

        svc.UpdateViewModelHotkey(null!, "SnipHotkey", "Ctrl+A");
        svc.UpdateViewModelHotkey(target, "", "Ctrl+A");
        svc.UpdateViewModelHotkey(target, null!, "Ctrl+A");

        Assert.Equal(string.Empty, target.SnipHotkey);
    }

    [Fact]
    public void ValidateTags_ReturnsOnlyTagsWithNoWritableStringProperty()
    {
        var svc = new HotkeyMappingService();
        var target = new FakeHotkeyTarget();

        var unknown = svc.ValidateTags(
            new[] { "SnipHotkey", "RecordHotkey", "Bogus", "ReadOnlyHotkey", "NotAString" },
            target);

        Assert.Equal(new[] { "Bogus", "ReadOnlyHotkey", "NotAString" }, unknown);
    }

    [Fact]
    public void ValidateTags_NullArgs_ReturnEmpty()
    {
        var svc = new HotkeyMappingService();

        Assert.Empty(svc.ValidateTags(null!, new FakeHotkeyTarget()));
        Assert.Empty(svc.ValidateTags(new[] { "SnipHotkey" }, null!));
    }
}
