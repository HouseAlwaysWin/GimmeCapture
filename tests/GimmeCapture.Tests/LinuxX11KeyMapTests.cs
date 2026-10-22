using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Linux;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="LinuxX11KeyMap"/> turns the hotkeys <see cref="HotkeyParsingHelper"/> parses into X11 grabs. The key
/// tests start from hotkey text, so a key the parser accepts but X11 cannot grab (keysym 0, which
/// <c>LinuxGlobalHotkeyService.GrabKey</c> skips without a word) fails here.
/// </summary>
public class LinuxX11KeyMapTests
{
    // Win32 MOD_* flags, as HotkeyParsingHelper produces them.
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    [Theory]
    [InlineData(ModAlt, LinuxX11KeyMap.Mod1Mask)]
    [InlineData(ModControl, LinuxX11KeyMap.ControlMask)]
    [InlineData(ModShift, LinuxX11KeyMap.ShiftMask)]
    [InlineData(ModWin, LinuxX11KeyMap.Mod4Mask)]
    public void Win32ToX11Mods_MapsEachModifier(uint win32, uint expected)
    {
        Assert.Equal(expected, LinuxX11KeyMap.Win32ToX11Mods(win32));
    }

    [Fact]
    public void Win32ToX11Mods_CombinesTheModifiersOfParsedHotkeyText()
    {
        var (mods, _) = HotkeyParsingHelper.ParseHotkey("Ctrl+Alt+Shift+F1");

        Assert.Equal(
            LinuxX11KeyMap.ControlMask | LinuxX11KeyMap.Mod1Mask | LinuxX11KeyMap.ShiftMask,
            LinuxX11KeyMap.Win32ToX11Mods(mods));
    }

    // HandleKeyPress keeps only RelevantModMask of an event's state before comparing it with a grab, so a modifier
    // that maps outside the mask could never match.
    [Fact]
    public void RelevantModMask_KeepsEveryModifierAHotkeyCanCarry()
    {
        Assert.Equal(
            LinuxX11KeyMap.RelevantModMask,
            LinuxX11KeyMap.Win32ToX11Mods(ModAlt | ModControl | ModShift | ModWin));
    }

    [Fact]
    public void LockCombos_CoverEveryCapsLockAndNumLockState()
    {
        Assert.Equal(
            new uint[] { 0, LinuxX11KeyMap.LockMask, LinuxX11KeyMap.Mod2Mask, LinuxX11KeyMap.LockMask | LinuxX11KeyMap.Mod2Mask },
            LinuxX11KeyMap.LockCombos.Order());
    }

    // GrabKey grabs a hotkey once per LockCombos entry; the masked state of a press under any of them must still be
    // exactly the grabbed modifiers, or the hotkey stops working while CapsLock or NumLock is on.
    [Theory]
    [InlineData("PrintScreen")]
    [InlineData("Ctrl+Shift+A")]
    [InlineData("Alt+F4")]
    public void APressWithCapsLockOrNumLockOn_StillMatchesItsGrab(string hotkey)
    {
        var (win32Mods, _) = HotkeyParsingHelper.ParseHotkey(hotkey);
        uint grabbedMods = LinuxX11KeyMap.Win32ToX11Mods(win32Mods);

        foreach (uint lockMask in LinuxX11KeyMap.LockCombos)
        {
            uint eventState = grabbedMods | lockMask;
            Assert.Equal(grabbedMods, eventState & LinuxX11KeyMap.RelevantModMask);
        }
    }

    [Theory]
    [InlineData("PrintScreen", 0xFF61)] // XK_Print
    [InlineData("Enter", 0xFF0D)]       // XK_Return
    [InlineData("Esc", 0xFF1B)]
    [InlineData("Tab", 0xFF09)]
    [InlineData("Space", 0x0020)]
    [InlineData("Delete", 0xFFFF)]
    [InlineData("Insert", 0xFF63)]
    [InlineData("Backspace", 0xFF08)]
    [InlineData("Home", 0xFF50)]
    [InlineData("End", 0xFF57)]
    [InlineData("PageUp", 0xFF55)]      // XK_Prior
    [InlineData("PageDown", 0xFF56)]    // XK_Next
    [InlineData("Left", 0xFF51)]
    [InlineData("Up", 0xFF52)]
    [InlineData("Right", 0xFF53)]
    [InlineData("Down", 0xFF54)]
    public void VkToKeysym_MapsEveryNamedKeyTheParserAccepts(string key, int expectedKeysym)
    {
        Assert.Equal((ulong)expectedKeysym, KeysymFor(key));
    }

    [Fact]
    public void VkToKeysym_MapsEveryLetterDigitAndFunctionKey()
    {
        for (char c = 'A'; c <= 'Z'; c++)
        {
            // The lowercase keysym names the physical key; Shift belongs to the grab's modifiers.
            Assert.Equal((ulong)char.ToLowerInvariant(c), KeysymFor(c.ToString()));
        }

        for (char c = '0'; c <= '9'; c++)
        {
            Assert.Equal((ulong)c, KeysymFor(c.ToString()));
        }

        for (int f = 1; f <= 24; f++)
        {
            Assert.Equal(0xFFBEUL + (ulong)(f - 1), KeysymFor($"F{f}")); // XK_F1 onwards
        }
    }

    [Theory]
    [InlineData("F25")]   // past the function keys the parser maps
    [InlineData("Pause")] // a key the parser does not know
    public void VkToKeysym_ReturnsZero_ForAKeyThatIsNeverGrabbed(string key)
    {
        Assert.Equal(0UL, KeysymFor(key));
    }

    private static ulong KeysymFor(string hotkey) =>
        LinuxX11KeyMap.VkToKeysym(HotkeyParsingHelper.ParseHotkey(hotkey).VirtualKey);
}
