namespace GimmeCapture.Models;

/// <summary>
/// A named bundle of Compress-tab settings the user can save and re-apply. Persisted as JSON by
/// <see cref="GimmeCapture.Services.Core.Infrastructure.CompressPresetService"/>.
/// </summary>
public sealed class CompressPreset
{
    public string Name { get; set; } = string.Empty;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public int MaxHeight { get; set; }            // 0 = original
    public int MaxFps { get; set; }               // 0 = source rate
    public string Preset { get; set; } = "veryfast";
    public string Format { get; set; } = "MP4";
    public int Crf { get; set; } = 23;
    public bool UseTargetSize { get; set; }
    public decimal TargetSizeMB { get; set; } = 25m;
    public bool DropAudio { get; set; }
    public int AudioBitrateKbps { get; set; }     // 0 = auto
    public int AudioChannels { get; set; } = 2;   // 1 = mono, 2 = stereo

    // HandBrake-style video filters (safe 1:1 subset). Off/false = no filter (original behaviour).
    public DenoiseMode Denoise { get; set; } = DenoiseMode.Off;
    public SharpenMode Sharpen { get; set; } = SharpenMode.Off;
    public bool Deblock { get; set; }
    public bool Grayscale { get; set; }
}
