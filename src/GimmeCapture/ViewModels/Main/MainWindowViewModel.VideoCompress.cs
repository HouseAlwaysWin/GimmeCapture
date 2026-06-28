using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

// "Compress Video" tab: import any video file from disk and re-encode it smaller through the
// existing in-process LibavClipExporter pipeline (H.264, VideoQuality -> CRF ladder 20/23/28).
// This is UI plumbing over machinery that already exists — no new encoding code.
public partial class MainWindowViewModel
{
    /// <summary>Set by the view: opens a file picker and returns the chosen video path (or null).</summary>
    public Func<Task<string?>>? PickCompressInputAction { get; set; }

    /// <summary>Set by the view: opens a save picker (arg = suggested file name) and returns the target path (or null).</summary>
    public Func<string, Task<string?>>? PickCompressOutputAction { get; set; }

    private string _compressInputPath = string.Empty;
    public string CompressInputPath
    {
        get => _compressInputPath;
        set => this.RaiseAndSetIfChanged(ref _compressInputPath, value);
    }

    private string _compressInputInfo = string.Empty;
    public string CompressInputInfo
    {
        get => _compressInputInfo;
        set => this.RaiseAndSetIfChanged(ref _compressInputInfo, value);
    }

    private bool _isCompressing;
    public bool IsCompressing
    {
        get => _isCompressing;
        private set => this.RaiseAndSetIfChanged(ref _isCompressing, value);
    }

    private string _compressStatusText = string.Empty;
    public string CompressStatusText
    {
        get => _compressStatusText;
        set => this.RaiseAndSetIfChanged(ref _compressStatusText, value);
    }

    // Quality picker reuses the same option type/localized strings (VideoQualityLow/Medium/High) as the Record tab.
    public RecordingSettingsViewModel.VideoQualityOption[] CompressQualityOptions { get; } =
    [
        new RecordingSettingsViewModel.VideoQualityOption { Value = VideoQuality.Low },
        new RecordingSettingsViewModel.VideoQualityOption { Value = VideoQuality.Medium },
        new RecordingSettingsViewModel.VideoQualityOption { Value = VideoQuality.High }
    ];

    private RecordingSettingsViewModel.VideoQualityOption? _selectedCompressQualityOption;
    public RecordingSettingsViewModel.VideoQualityOption? SelectedCompressQualityOption
    {
        get => _selectedCompressQualityOption;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressQualityOption, value);
    }

    // Codec picker (H.264 / H.265) reuses the same option type/localized strings as the Record tab.
    public RecordingSettingsViewModel.VideoCodecOption[] CompressCodecOptions { get; } =
    [
        new RecordingSettingsViewModel.VideoCodecOption { Value = VideoCodec.H264 },
        new RecordingSettingsViewModel.VideoCodecOption { Value = VideoCodec.H265 }
    ];

    private RecordingSettingsViewModel.VideoCodecOption? _selectedCompressCodecOption;
    public RecordingSettingsViewModel.VideoCodecOption? SelectedCompressCodecOption
    {
        get => _selectedCompressCodecOption;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressCodecOption, value);
    }

    // Target-size mode: encode at a computed average bitrate so the output lands near a chosen file size.
    private bool _compressUseTargetSize;
    public bool CompressUseTargetSize
    {
        get => _compressUseTargetSize;
        set => this.RaiseAndSetIfChanged(ref _compressUseTargetSize, value);
    }

    // decimal to bind 1:1 with NumericUpDown.Value (decimal?); converted to double for the bitrate math.
    private decimal _compressTargetSizeMB = 25m;
    public decimal CompressTargetSizeMB
    {
        get => _compressTargetSizeMB;
        set => this.RaiseAndSetIfChanged(ref _compressTargetSizeMB, value);
    }

    // Output container — only what LibavClipExporter.ContainerForExtension supports.
    public string[] CompressOutputFormats { get; } = ["MP4", "MKV", "MOV"];

    private string _selectedCompressFormat = "MP4";
    public string SelectedCompressFormat
    {
        get => _selectedCompressFormat;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressFormat, value);
    }

    public ReactiveCommand<Unit, Unit> PickCompressInputCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CompressCommand { get; private set; } = null!;

    private void InitializeVideoCompress()
    {
        // Default the quality to whatever the recording settings currently use, for a sensible starting point.
        VideoQuality initial = RecordingSettings.VideoQuality;
        _selectedCompressQualityOption = Array.Find(CompressQualityOptions, o => o.Value == initial)
            ?? Array.Find(CompressQualityOptions, o => o.Value == VideoQuality.Medium);

        VideoCodec initialCodec = RecordingSettings.VideoCodec;
        _selectedCompressCodecOption = Array.Find(CompressCodecOptions, o => o.Value == initialCodec)
            ?? Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H264);

        CompressStatusText = LocalizationService.Instance["CompressStatusReady"];

        PickCompressInputCommand = ReactiveCommand.CreateFromTask(
            PickCompressInputAsync,
            this.WhenAnyValue(x => x.IsCompressing, busy => !busy));

        var canCompress = this.WhenAnyValue(
            x => x.CompressInputPath,
            x => x.IsCompressing,
            (path, busy) => !string.IsNullOrEmpty(path) && !busy);
        CompressCommand = ReactiveCommand.CreateFromTask(CompressAsync, canCompress);

        PickCompressInputCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Pick", ex));
        CompressCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Run", ex));
    }

    private async Task PickCompressInputAsync()
    {
        if (PickCompressInputAction == null)
        {
            return;
        }

        string? path = await PickCompressInputAction();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        CompressInputPath = path;
        CompressInputInfo = await BuildInputInfoAsync(path);
        CompressStatusText = LocalizationService.Instance["CompressStatusReady"];
    }

    private static async Task<string> BuildInputInfoAsync(string path)
    {
        string name = Path.GetFileName(path);
        string size = FormatFileSize(new FileInfo(path).Length);

        string duration = "--:--";
        try
        {
            using var probe = new LibavVideoFramePlayer();
            double? seconds = await probe.ProbeDurationSecondsAsync(path);
            if (seconds is > 0)
            {
                duration = TimeSpan.FromSeconds(seconds.Value).ToString(@"hh\:mm\:ss");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.Probe", ex);
        }

        return $"{name}  ·  {duration}  ·  {size}";
    }

    private async Task CompressAsync()
    {
        if (IsCompressing || string.IsNullOrEmpty(CompressInputPath) || !File.Exists(CompressInputPath))
        {
            return;
        }

        if (PickCompressOutputAction == null)
        {
            return;
        }

        string ext = "." + SelectedCompressFormat.ToLowerInvariant();
        string suggested = Path.GetFileNameWithoutExtension(CompressInputPath) + "_compressed" + ext;

        string? outputPath = await PickCompressOutputAction(suggested);
        if (string.IsNullOrEmpty(outputPath))
        {
            return;
        }

        if (LibavClipExporter.ContainerForExtension(Path.GetExtension(outputPath)) == null)
        {
            CompressStatusText = LocalizationService.Instance["StatusCompressFailed"];
            ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Error);
            return;
        }

        VideoQuality quality = SelectedCompressQualityOption?.Value ?? VideoQuality.Medium;
        VideoCodec codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264;
        string input = CompressInputPath;

        double probedDuration = 0;
        try
        {
            using var durationProbe = new LibavVideoFramePlayer();
            probedDuration = await durationProbe.ProbeDurationSecondsAsync(input) ?? 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeDuration", ex);
        }

        // Target-size mode needs a known duration to turn a file size into an average bitrate.
        int targetBitrateKbps = 0;
        if (CompressUseTargetSize)
        {
            if (probedDuration <= 0)
            {
                CompressStatusText = LocalizationService.Instance["CompressTargetNeedsDuration"];
                ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Error);
                return;
            }

            targetBitrateKbps = ComputeTargetVideoBitrateKbps((double)CompressTargetSizeMB, probedDuration);
        }

        IsCompressing = true;
        CompressStatusText = LocalizationService.Instance["StatusCompressing"];

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 0 duration (unreadable) falls back to a large-but-sane end (24h) so the whole file is
            // exported without risking an oversized duration-derived buffer inside the exporter.
            double end = probedDuration > 0 ? probedDuration : 24 * 60 * 60;
            var ranges = new[] { new LibavClipExporter.SourceRange(0, end) };

            Directory.CreateDirectory(tempDir);
            string tempOut = Path.Combine(tempDir, "compressed" + Path.GetExtension(outputPath));

            bool ok = await Task.Run(() =>
                LibavClipExporter.TryExport(input, ranges, tempOut, quality,
                    codec: codec, targetVideoBitrateKbps: targetBitrateKbps));

            if (ok && File.Exists(tempOut) && new FileInfo(tempOut).Length > 0)
            {
                File.Copy(tempOut, outputPath, true);

                long before = new FileInfo(input).Length;
                long after = new FileInfo(outputPath).Length;
                CompressStatusText =
                    $"{LocalizationService.Instance["StatusCompressDone"]}  ({FormatFileSize(before)} → {FormatFileSize(after)})";
                ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Success);
                FileLocationService.RevealInFileExplorer(outputPath);
            }
            else
            {
                CompressStatusText = LocalizationService.Instance["StatusCompressFailed"];
                ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.Export", ex);
            CompressStatusText = LocalizationService.Instance["StatusCompressFailed"];
            ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Error);
        }
        finally
        {
            IsCompressing = false;
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.Cleanup", ex);
            }
        }
    }

    /// <summary>
    /// Turns a desired output size (MB) and a duration (seconds) into an average video bitrate (kbps),
    /// reserving a fixed audio allowance and a small safety margin. Single-pass ABR is approximate, so
    /// the real file lands near (usually a touch under) the target.
    /// </summary>
    internal static int ComputeTargetVideoBitrateKbps(double targetSizeMB, double durationSeconds)
    {
        if (targetSizeMB <= 0 || durationSeconds <= 0)
        {
            return 0;
        }

        const double audioKbps = 128;   // reserve for the AAC track the exporter muxes in
        const double safety = 0.97;     // headroom for container overhead / ABR overshoot

        double totalKbps = targetSizeMB * 1024.0 * 1024.0 * 8.0 / 1000.0 / durationSeconds;
        double videoKbps = (totalKbps - audioKbps) * safety;

        // Never go below a usable floor (very large files at long durations can compute tiny bitrates).
        return (int)Math.Max(50, Math.Floor(videoKbps));
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.0} {units[unit]}";
    }
}
