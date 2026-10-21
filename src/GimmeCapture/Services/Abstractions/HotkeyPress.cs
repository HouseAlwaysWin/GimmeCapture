namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// A global hotkey press. <paramref name="PressedAtTimestamp"/> is the <see cref="System.Diagnostics.Stopwatch"/>
/// timestamp of the key press itself, taken before the press is handed to the UI thread, so a handler can tell how
/// long the press waited for it.
/// </summary>
public readonly record struct HotkeyPress(int Id, long PressedAtTimestamp);
