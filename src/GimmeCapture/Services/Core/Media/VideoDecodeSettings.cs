namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Whether video is decoded on the graphics card (D3D11VA) where it can be: <c>AppSettings.HardwareDecodeEnabled</c>,
/// which a user with a flaky GPU driver turns off. Apart from <see cref="NativeFFmpeg.LibavVideoFramePlayer"/>, whose
/// static fields hold FFmpeg's types: setting the flag there ran its static constructor, so applying the settings at
/// start-up loaded FFmpeg's bindings (FFmpeg.AutoGen.dll, 1.1 MB) before the hotkeys worked, which no start needs.
/// </summary>
internal static class VideoDecodeSettings
{
    private static volatile bool _hardwareDecodeEnabled = true;

    public static bool HardwareDecodeEnabled
    {
        get => _hardwareDecodeEnabled;
        set => _hardwareDecodeEnabled = value;
    }
}
