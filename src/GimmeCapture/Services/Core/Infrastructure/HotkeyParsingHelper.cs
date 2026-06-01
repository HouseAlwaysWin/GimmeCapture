using System;

namespace GimmeCapture.Services.Core.Infrastructure;

internal static class HotkeyParsingHelper
{
    public static (uint Modifiers, uint VirtualKey) ParseHotkey(ReadOnlySpan<char> hotkey)
    {
        hotkey = hotkey.Trim();
        uint modifiers = 0;

        if (hotkey.Contains("Ctrl".AsSpan(), StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0002;
        if (hotkey.Contains("Alt".AsSpan(), StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0001;
        if (hotkey.Contains("Shift".AsSpan(), StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0004;

        ReadOnlySpan<char> keyPart = GetKeyPart(hotkey);
        return (modifiers, ParseVirtualKey(keyPart));
    }

    public static bool IsSafeSingleKeyHotkey(ReadOnlySpan<char> hotkey)
    {
        hotkey = hotkey.Trim();
        if (hotkey.IsEmpty)
        {
            return false;
        }

        int plusIndex = hotkey.IndexOf('+');
        if (plusIndex >= 0)
        {
            return false;
        }

        ReadOnlySpan<char> keyPart = NormalizeBaseKey(hotkey);
        if (TryParseFunctionKey(keyPart, out int functionKey))
        {
            return functionKey is >= 1 and <= 24;
        }

        return keyPart.Equals("Escape".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("Esc".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PrintScreen".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("Print".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PrtSc".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    public static ReadOnlySpan<char> NormalizeBaseKey(ReadOnlySpan<char> keyPart)
    {
        keyPart = keyPart.Trim();
        if (keyPart.Length == 2 && keyPart[0] is 'D' or 'd' && char.IsDigit(keyPart[1]))
        {
            keyPart = keyPart[1..];
        }

        if (keyPart.Equals("Return".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return "Enter".AsSpan();
        }

        return keyPart;
    }

    public static ReadOnlySpan<char> GetKeyPart(ReadOnlySpan<char> hotkey)
    {
        int plusIndex = hotkey.LastIndexOf('+');
        return NormalizeBaseKey(plusIndex >= 0 ? hotkey[(plusIndex + 1)..] : hotkey);
    }

    public static bool ModifiersMatch(ReadOnlySpan<char> hotkey, bool ctrlDown, bool altDown, bool shiftDown)
    {
        return ContainsModifier(hotkey, "Ctrl".AsSpan()) == ctrlDown
            && ContainsModifier(hotkey, "Alt".AsSpan()) == altDown
            && ContainsModifier(hotkey, "Shift".AsSpan()) == shiftDown;
    }

    private static bool ContainsModifier(ReadOnlySpan<char> hotkey, ReadOnlySpan<char> modifier)
        => hotkey.Contains(modifier, StringComparison.OrdinalIgnoreCase);

    private static uint ParseVirtualKey(ReadOnlySpan<char> keyPart)
    {
        keyPart = NormalizeBaseKey(keyPart);

        if (TryParseFunctionKey(keyPart, out int functionKey))
        {
            return functionKey is >= 1 and <= 24 ? (uint)(0x70 + functionKey - 1) : 0;
        }

        if (keyPart.Equals("PRINTSCREEN".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PRTSC".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PRINT".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x2C;
        }

        if (keyPart.Equals("ENTER".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x0D;
        }

        if (keyPart.Equals("ESC".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("ESCAPE".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x1B;
        }

        if (keyPart.Equals("TAB".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x09;
        }

        if (keyPart.Equals("SPACE".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x20;
        }

        if (keyPart.Equals("DELETE".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("DEL".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x2E;
        }

        if (keyPart.Equals("INSERT".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("INS".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x2D;
        }

        if (keyPart.Equals("BACKSPACE".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("BKSP".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 0x08;
        }

        if (keyPart.Equals("HOME".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x24;
        if (keyPart.Equals("END".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x23;
        if (keyPart.Equals("PAGEUP".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PGUP".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x21;
        if (keyPart.Equals("PAGEDOWN".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || keyPart.Equals("PGDN".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x22;
        if (keyPart.Equals("LEFT".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x25;
        if (keyPart.Equals("UP".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x26;
        if (keyPart.Equals("RIGHT".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x27;
        if (keyPart.Equals("DOWN".AsSpan(), StringComparison.OrdinalIgnoreCase)) return 0x28;

        return keyPart.Length == 1 && char.IsLetterOrDigit(keyPart[0])
            ? (uint)char.ToUpperInvariant(keyPart[0])
            : 0;
    }

    private static bool TryParseFunctionKey(ReadOnlySpan<char> keyPart, out int functionKey)
    {
        functionKey = 0;
        return keyPart.Length > 1
            && (keyPart[0] is 'F' or 'f')
            && int.TryParse(keyPart[1..], out functionKey);
    }
}
