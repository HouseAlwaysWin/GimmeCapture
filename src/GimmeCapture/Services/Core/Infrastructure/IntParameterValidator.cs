using System;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Pure clamping helpers for the integer settings exposed by
/// <c>MainWindowViewModel.Settings</c>. Extracted so the exact bounds can be
/// unit tested without a ViewModel / UI thread. Behavior-preserving — the
/// bounds match the inline <see cref="Math.Clamp(int,int,int)"/> / <see cref="Math.Max(int,int)"/>
/// calls that previously lived in the property setters.
/// </summary>
public static class IntParameterValidator
{
    /// <summary>Llama context size, clamped to the supported [512, 8192] range.</summary>
    public static int ClampLlamaContextSize(int value) => Math.Clamp(value, 512, 8192);

    /// <summary>Playback FPS (UI or timeline), clamped to [1, 120].</summary>
    public static int ClampPlaybackFps(int value) => Math.Clamp(value, 1, 120);

    /// <summary>GPU layer count, floored at 0 (no upper bound).</summary>
    public static int ClampGpuLayers(int value) => Math.Max(0, value);
}
