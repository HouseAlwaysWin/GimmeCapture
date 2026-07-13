using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// An immutable snapshot of the Compress tab's output settings, taken on the UI thread when an encode starts (so
/// the worker thread sees a stable set even if the user keeps tweaking). Promoted out of MainWindowViewModel so
/// the compress pipeline can consume it directly. <see cref="ToExportOptions"/> maps it onto the encoder options.
/// </summary>
internal readonly record struct CompressSettingsSnapshot(
    VideoCodec Codec, int Crf, int MaxHeight, int MaxFps, string Preset,
    bool DropAudio, int AudioBitrateKbps, int AudioChannels,
    bool UseTargetSize, decimal TargetSizeMB, bool UseTwoPass,
    DenoiseMode Denoise, SharpenMode Sharpen, bool Deblock, bool Grayscale)
{
    /// <summary>
    /// Build the <see cref="LibavExportOptions"/> for a straight snapshot-driven encode. The context-specific
    /// knobs (rotation, ABR target bitrate, two-pass) are passed per call. The intermediate GIF/WebM render,
    /// the CRF chunk, and the audio-only finalize mux each set non-snapshot fields and build their own options.
    /// </summary>
    public LibavExportOptions ToExportOptions(int rotationDegrees = 0, int targetVideoBitrateKbps = 0, bool twoPass = false)
        => new LibavExportOptions
        {
            Codec = Codec,
            CrfOverride = Crf,
            Preset = Preset,
            MaxHeight = MaxHeight,
            MaxFps = MaxFps,
            DropAudio = DropAudio,
            AudioBitrateKbps = AudioBitrateKbps,
            AudioChannels = AudioChannels,
            Denoise = Denoise,
            Sharpen = Sharpen,
            Deblock = Deblock,
            Grayscale = Grayscale,
            RotationDegrees = rotationDegrees,
            TargetVideoBitrateKbps = targetVideoBitrateKbps,
            TwoPass = twoPass,
        };
}
