using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Decides, for each key event the global keyboard hook, raw input and WM_HOTKEY report, whether it completes a
/// registered hotkey: the press the Windows hotkey service then fires and swallows. One press can arrive through
/// all three, so it counts once: a held key does not match again until it is released, and a hotkey that just fired
/// is ignored for <see cref="DebounceMs"/>. Not thread-safe; the hotkey service calls it from its hook thread only.
/// </summary>
internal sealed class LowLevelHotkeyMatcher
{
    internal const long DebounceMs = 150;

    // RegisterHotKey's modifier flags, which the registered combos use.
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;

    private readonly HashSet<int> _pressedKeys = new();
    private readonly Dictionary<int, long> _lastFiredMs = new();
    private bool _shiftDown;
    private bool _ctrlDown;
    private bool _altDown;

    /// <summary>
    /// Feeds one key event and returns the id of the hotkey it completes, or null to let the key through: always
    /// the case for modifier keys, key-ups, auto-repeats and while <paramref name="suspended"/>.
    /// <paramref name="otherHeldModifiers"/> reports modifiers held outside the events this matcher has seen; it is
    /// only asked for a fresh key press.
    /// </summary>
    internal int? OnKey(
        int virtualKey,
        bool isKeyDown,
        bool isKeyUp,
        Func<uint> otherHeldModifiers,
        IReadOnlyDictionary<int, (uint Mods, uint Key)> registered,
        bool suspended,
        long nowMs)
    {
        if (TrackModifier(virtualKey, isKeyDown, isKeyUp))
        {
            return null;
        }

        if (isKeyUp)
        {
            _pressedKeys.Remove(virtualKey);
            return null;
        }

        // Auto-repeat sends more key-downs without a key-up; only the first one counts. While suspended (the hotkeys
        // page is recording a new combo) the key is still tracked but must reach the app.
        if (!isKeyDown || !_pressedKeys.Add(virtualKey) || suspended)
        {
            return null;
        }

        uint modifiers = HeldModifiers() | otherHeldModifiers();
        foreach (var (id, combo) in registered)
        {
            if (combo.Key == (uint)virtualKey && combo.Mods == modifiers && TryFire(id, nowMs))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Records a firing of <paramref name="id"/> unless it fired less than <see cref="DebounceMs"/> ago, which means
    /// the same press arriving through a second source. WM_HOTKEY comes straight here.
    /// </summary>
    internal bool TryFire(int id, long nowMs)
    {
        if (_lastFiredMs.TryGetValue(id, out long last) && nowMs - last < DebounceMs)
        {
            return false;
        }

        _lastFiredMs[id] = nowMs;
        return true;
    }

    /// <summary>Forgets when <paramref name="id"/> last fired, once it is unregistered.</summary>
    internal void Forget(int id) => _lastFiredMs.Remove(id);

    /// <summary>Forgets every held key, modifier and firing, once the hook that reported them is gone.</summary>
    internal void Reset()
    {
        _pressedKeys.Clear();
        _lastFiredMs.Clear();
        _shiftDown = false;
        _ctrlDown = false;
        _altDown = false;
    }

    private uint HeldModifiers() =>
        (_shiftDown ? ModShift : 0) | (_ctrlDown ? ModControl : 0) | (_altDown ? ModAlt : 0);

    // Shift, Ctrl and Alt in their generic and left/right forms: tracked, never matched themselves.
    private bool TrackModifier(int virtualKey, bool isKeyDown, bool isKeyUp)
    {
        bool isDown = isKeyDown && !isKeyUp;
        switch (virtualKey)
        {
            case 0x10 or 0xA0 or 0xA1:
                _shiftDown = isDown;
                return true;
            case 0x11 or 0xA2 or 0xA3:
                _ctrlDown = isDown;
                return true;
            case 0x12 or 0xA4 or 0xA5:
                _altDown = isDown;
                return true;
            default:
                return false;
        }
    }
}
