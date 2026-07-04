namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// Shared mapping from the app's Win32-style hotkey codes (as produced by
/// <see cref="GimmeCapture.Services.Core.Infrastructure.HotkeyParsingHelper"/>) to X11 keysyms and
/// modifier masks. Used by <see cref="LinuxGlobalHotkeyService"/> (global hotkeys) and
/// <see cref="LinuxSnipKeyGrab"/> (transient snip-overlay key grabs).
/// </summary>
internal static class LinuxX11KeyMap
{
    // X11 modifier masks.
    public const uint ShiftMask = 1 << 0;
    public const uint LockMask = 1 << 1;   // CapsLock
    public const uint ControlMask = 1 << 2;
    public const uint Mod1Mask = 1 << 3;   // Alt
    public const uint Mod2Mask = 1 << 4;   // NumLock (typically)
    public const uint Mod4Mask = 1 << 6;   // Super/Win
    public const uint RelevantModMask = ShiftMask | ControlMask | Mod1Mask | Mod4Mask;

    /// <summary>Grab under every Lock/NumLock combination so a key fires regardless of those toggles.</summary>
    public static readonly uint[] LockCombos = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };

    public static uint Win32ToX11Mods(uint win32)
    {
        // HotkeyParsingHelper uses Win32 MOD_* flags: ALT=0x1, CONTROL=0x2, SHIFT=0x4, WIN=0x8.
        uint m = 0;
        if ((win32 & 0x0001) != 0) m |= Mod1Mask;
        if ((win32 & 0x0002) != 0) m |= ControlMask;
        if ((win32 & 0x0004) != 0) m |= ShiftMask;
        if ((win32 & 0x0008) != 0) m |= Mod4Mask;
        return m;
    }

    public static ulong VkToKeysym(uint vk)
    {
        // F1..F24 (VK 0x70..0x87) -> XK_F1 (0xFFBE)..
        if (vk >= 0x70 && vk <= 0x87) return 0xFFBEUL + (vk - 0x70);
        // A..Z (VK 0x41..0x5A) -> lowercase keysym a..z (0x61..0x7A) for the physical key
        if (vk >= 0x41 && vk <= 0x5A) return 0x61UL + (vk - 0x41);
        // 0..9 (VK 0x30..0x39) -> XK_0..XK_9 (identical to ASCII)
        if (vk >= 0x30 && vk <= 0x39) return vk;

        return vk switch
        {
            0x2C => 0xFF61, // PrintScreen -> XK_Print
            0x0D => 0xFF0D, // Enter -> XK_Return
            0x1B => 0xFF1B, // Escape
            0x09 => 0xFF09, // Tab
            0x20 => 0x0020, // Space
            0x2E => 0xFFFF, // Delete
            0x2D => 0xFF63, // Insert
            0x08 => 0xFF08, // BackSpace
            0x24 => 0xFF50, // Home
            0x23 => 0xFF57, // End
            0x21 => 0xFF55, // Page_Up
            0x22 => 0xFF56, // Page_Down
            0x25 => 0xFF51, // Left
            0x26 => 0xFF52, // Up
            0x27 => 0xFF53, // Right
            0x28 => 0xFF54, // Down
            _ => 0
        };
    }
}
