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

    // Temporary hotkeys registered only while the capture overlay is deliberately unfocused
    // (CaptureWithoutStealingFocus): the low-level keyboard hook that normally serves these keys is silenced by
    // an elevated foreground window, while RegisterHotKey still fires. See SnipWindowViewModel.UnfocusedHotkeys.cs.
    public const int OverlayDismiss = 9009;
    public const int OverlayAction = 9010;
    public const int OverlayCopy = 9011;
    public const int OverlaySave = 9012;
    public const int OverlayRecordPause = 9013;
    public const int OverlayRecordStop = 9014;

    /// <summary>
    /// Ids registered only for the lifetime of an overlay or a scrolling session, mirroring keys the low-level
    /// keyboard hook already serves so they keep working over an elevated foreground window. Another app owning
    /// the combo is an expected outcome for these — the hook still handles it — so a failure must not be
    /// reported to the user the way a configured hotkey's failure is.
    /// </summary>
    public static bool IsTemporaryOverlayHotkey(int id) =>
        id is ScrollingCaptureFinish or ScrollingCaptureCancel
            or OverlayDismiss or OverlayAction or OverlayCopy or OverlaySave
            or OverlayRecordPause or OverlayRecordStop;
}
