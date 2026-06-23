namespace GimmeCapture.Services.Core.Infrastructure;

public static class HotkeyIds
{
    public const int Snip = 9000;
    public const int Copy = 9001;
    public const int Pin = 9002;
    public const int Record = 9003;
    public const int Translate = 9004;
    public const int TextCopy = 9005;
    public const int ScrollingCapture = 9006;

    // Temporary hotkeys registered only while a manual scrolling-capture session is active,
    // so finish (Pin key) / cancel (Close key) work even when the target window is focused
    // (the low-level keyboard hook can be blocked by an elevated foreground window).
    public const int ScrollingCaptureFinish = 9007;
    public const int ScrollingCaptureCancel = 9008;
}
