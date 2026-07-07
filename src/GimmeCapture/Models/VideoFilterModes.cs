namespace GimmeCapture.Models;

// HandBrake-style compress video filters (the safe, frame-count-preserving 1:1 subset). Each maps to a
// libavfilter node in LibavClipExporter.BuildVideoFilterChain. Deinterlace / detelecine (which change the
// frame count) are intentionally excluded.

/// <summary>Spatial+temporal denoise strength. Off = no filter. NLMeans is slower but higher quality.</summary>
public enum DenoiseMode
{
    Off,
    Light,
    Medium,
    Strong,
    NLMeans,
}

/// <summary>Unsharp-mask sharpen strength. Off = no filter.</summary>
public enum SharpenMode
{
    Off,
    Light,
    Medium,
    Strong,
}
