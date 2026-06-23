using System.Collections.Generic;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class HotkeyConflictValidatorTests
{
    private static Dictionary<string, string> Map(params (string Tag, string Value)[] entries)
    {
        var d = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var (tag, value) in entries)
        {
            d[tag] = value;
        }
        return d;
    }

    [Fact]
    public void NoConflict_ReturnsNull()
    {
        var current = Map(("Snip_Rectangle", "R"));
        Assert.Null(HotkeyConflictValidator.FindConflict("Snip_Ellipse", "E", current));
    }

    [Fact]
    public void SnipGroup_DetectsInGroupConflict()
    {
        var current = Map(("Snip_Rectangle", "R"));
        Assert.Equal("TipRectangle", HotkeyConflictValidator.FindConflict("Snip_Ellipse", "R", current));
    }

    [Fact]
    public void RecordGroup_DetectsInGroupConflict()
    {
        var current = Map(("Record_Rectangle", "R"));
        Assert.Equal("TipRectangle", HotkeyConflictValidator.FindConflict("Record_Ellipse", "R", current));
    }

    [Fact]
    public void TranslateGroup_DetectsInGroupConflict()
    {
        var current = Map(("Translate_Toolbar", "T"));
        Assert.Equal("ActionToolbar", HotkeyConflictValidator.FindConflict("Translate_Close", "T", current));
    }

    [Fact]
    public void GlobalGroup_DetectsConflictAmongTheFourTriggers()
    {
        var current = Map(("SnipHotkey", "Shift+F1"));
        Assert.Equal("SnipHotkey", HotkeyConflictValidator.FindConflict("RecordHotkey", "Shift+F1", current));
    }

    [Fact]
    public void GlobalGroup_PinAndCopyAreNotConflictSources()
    {
        // PinHotkey / CopyHotkey are in the gate list but are never checked as sources.
        var current = Map(("PinHotkey", "F6"), ("CopyHotkey", "F6"));
        Assert.Null(HotkeyConflictValidator.FindConflict("SnipHotkey", "F6", current));
    }

    [Fact]
    public void InGroupComparison_IsCaseSensitive()
    {
        var current = Map(("SnipHotkey", "f6"));
        Assert.Null(HotkeyConflictValidator.FindConflict("RecordHotkey", "F6", current));
    }

    [Fact]
    public void CrossAction_IsCaseInsensitive()
    {
        var current = Map(("Translate_Action", "F6"));
        Assert.Equal("ActionHideTranslate", HotkeyConflictValidator.FindConflict("SnipHotkey", "f6", current));
    }

    // Mirrors MainWindowViewModelHotkeyTests.CheckHotkeyConflict_Finds_New_DefaultActionKeyConflicts
    [Theory]
    [InlineData("Translate_Action", "F6", "MenuPinTranslation")]
    [InlineData("Record_Action", "F6", null)]
    [InlineData("Translate_Pin", "F8", "ActionHideTranslate")]
    [InlineData("Snip_Pin", "F6", null)]
    public void UnifiedPinAndCrossAction_MatchViewModelBehavior(string targetTag, string hotkey, string? expected)
    {
        var current = Map(
            ("Snip_Pin", "F6"),
            ("Record_Action", "F6"),
            ("Translate_Action", "F8"),
            ("Translate_Pin", "F6"));

        Assert.Equal(expected, HotkeyConflictValidator.FindConflict(targetTag, hotkey, current));
    }
}
