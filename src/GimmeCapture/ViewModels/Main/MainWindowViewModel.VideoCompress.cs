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

// "Compress Video" tab: import any video file from disk and re-encode it smaller through the in-process
// LibavClipExporter pipeline. Exposes codec, CRF/target-size, preset, downscale and drop-audio knobs.
public partial class MainWindowViewModel
{
    /// <summary>Set by the view: opens a file picker and returns the chosen video path (or null).</summary>
    public Func<Task<string?>>? PickCompressInputAction { get; set; }

    /// <summary>Set by the view: opens a Save dialog seeded with the suggested path; returns the chosen path (or null).</summary>
    public Func<string, Task<string?>>? PickCompressOutputAction { get; set; }

    // Custom output path. Empty = auto-derive next to the source ("<name>_compressed.<ext>").
    private string _compressOutputPath = string.Empty;
    public string CompressOutputPath
    {
        get => _compressOutputPath;
        set => this.RaiseAndSetIfChanged(ref _compressOutputPath, value);
    }

    // Cancels the in-flight encode (passed as the export CancellationToken).
    private CancellationTokenSource? _compressCts;

    // Pauses the in-flight encode: signaled = running, reset = the encode loop blocks between frames.
    private ManualResetEventSlim? _compressPauseGate;

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

    // Probed source metadata (set when a file is picked) used by the live size estimate.
    private int _sourceWidth;
    private int _sourceHeight;
    private int _sourceFps;
    private double _sourceDurationSeconds;

    // Live "estimated output size" text, recomputed as settings change. Empty when no source is loaded.
    private string _compressEstimateText = string.Empty;
    public string CompressEstimateText
    {
        get => _compressEstimateText;
        private set => this.RaiseAndSetIfChanged(ref _compressEstimateText, value);
    }

    private bool _isCompressing;
    public bool IsCompressing
    {
        get => _isCompressing;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isCompressing, value);
            this.RaisePropertyChanged(nameof(ShowPause));
            this.RaisePropertyChanged(nameof(ShowResume));
        }
    }

    // True while a running encode is paused. Drives the Pause/Resume button swap.
    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            this.RaisePropertyChanged(nameof(ShowPause));
            this.RaisePropertyChanged(nameof(ShowResume));
        }
    }

    // Show Pause while running and not paused; show Resume while running and paused.
    public bool ShowPause => IsCompressing && !IsPaused;
    public bool ShowResume => IsCompressing && IsPaused;

    // Video-encode progress (0..1) reported by the exporter; drives the determinate compress progress bar.
    private double _compressProgress;
    public double CompressProgress
    {
        get => _compressProgress;
        private set => this.RaiseAndSetIfChanged(ref _compressProgress, value);
    }

    private string _compressStatusText = string.Empty;
    public string CompressStatusText
    {
        get => _compressStatusText;
        set => this.RaiseAndSetIfChanged(ref _compressStatusText, value);
    }

    // Quality is controlled by a CRF slider (lower = better quality / larger file). 18-28 is the useful
    // range; 23 is a sensible default. Ignored in target-size mode (bitrate drives quality there).
    private int _compressCrf = 23;
    public int CompressCrf
    {
        get => _compressCrf;
        set => this.RaiseAndSetIfChanged(ref _compressCrf, value);
    }

    // Optional downscale. MaxHeight 0 = keep source resolution.
    public sealed class CompressResolutionOption
    {
        public int MaxHeight { get; init; }
        public string Label => MaxHeight <= 0
            ? LocalizationService.Instance["CompressResolutionOriginal"]
            : $"{MaxHeight}p";
    }

    public CompressResolutionOption[] CompressResolutionOptions { get; } =
    [
        new CompressResolutionOption { MaxHeight = 0 },
        new CompressResolutionOption { MaxHeight = 1080 },
        new CompressResolutionOption { MaxHeight = 720 },
        new CompressResolutionOption { MaxHeight = 480 }
    ];

    private CompressResolutionOption? _selectedCompressResolution;
    public CompressResolutionOption? SelectedCompressResolution
    {
        get => _selectedCompressResolution;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressResolution, value);
    }

    // Optional frame-rate cap. Fps 0 = keep the source rate.
    public sealed class CompressFpsOption
    {
        public int Fps { get; init; }
        public string Label => Fps <= 0
            ? LocalizationService.Instance["CompressFpsOriginal"]
            : $"{Fps} fps";
    }

    public CompressFpsOption[] CompressFpsOptions { get; } =
    [
        new CompressFpsOption { Fps = 0 },
        new CompressFpsOption { Fps = 60 },
        new CompressFpsOption { Fps = 30 },
        new CompressFpsOption { Fps = 24 },
        new CompressFpsOption { Fps = 15 }
    ];

    private CompressFpsOption? _selectedCompressFps;
    public CompressFpsOption? SelectedCompressFps
    {
        get => _selectedCompressFps;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressFps, value);
    }

    // Encoder speed/efficiency preset (slower = smaller for the same quality).
    public string[] CompressPresetOptions { get; } = ["ultrafast", "veryfast", "fast", "medium", "slow"];

    private string _selectedCompressPreset = "veryfast";
    public string SelectedCompressPreset
    {
        get => _selectedCompressPreset;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressPreset, value);
    }

    private bool _compressDropAudio;
    public bool CompressDropAudio
    {
        get => _compressDropAudio;
        set => this.RaiseAndSetIfChanged(ref _compressDropAudio, value);
    }

    // Audio bitrate. Kbps 0 = Auto (derive from quality). Ignored when audio is dropped.
    public sealed class CompressAudioBitrateOption
    {
        public int Kbps { get; init; }
        public string Label => Kbps <= 0
            ? LocalizationService.Instance["CompressAudioBitrateAuto"]
            : $"{Kbps} kbps";
    }

    public CompressAudioBitrateOption[] CompressAudioBitrateOptions { get; } =
    [
        new CompressAudioBitrateOption { Kbps = 0 },
        new CompressAudioBitrateOption { Kbps = 192 },
        new CompressAudioBitrateOption { Kbps = 128 },
        new CompressAudioBitrateOption { Kbps = 96 },
        new CompressAudioBitrateOption { Kbps = 64 }
    ];

    private CompressAudioBitrateOption? _selectedCompressAudioBitrate;
    public CompressAudioBitrateOption? SelectedCompressAudioBitrate
    {
        get => _selectedCompressAudioBitrate;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressAudioBitrate, value);
    }

    // Audio channels: stereo (2) or mono mixdown (1). Ignored when audio is dropped.
    public sealed class CompressAudioChannelsOption
    {
        public int Channels { get; init; }
        public string Label => Channels == 1
            ? LocalizationService.Instance["CompressAudioMono"]
            : LocalizationService.Instance["CompressAudioStereo"];
    }

    public CompressAudioChannelsOption[] CompressAudioChannelsOptions { get; } =
    [
        new CompressAudioChannelsOption { Channels = 2 },
        new CompressAudioChannelsOption { Channels = 1 }
    ];

    private CompressAudioChannelsOption? _selectedCompressAudioChannels;
    public CompressAudioChannelsOption? SelectedCompressAudioChannels
    {
        get => _selectedCompressAudioChannels;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressAudioChannels, value);
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
    public ReactiveCommand<Unit, Unit> PickCompressOutputCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CompressCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelCompressCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PauseCompressCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ResumeCompressCommand { get; private set; } = null!;

    private void InitializeVideoCompress()
    {
        _selectedCompressResolution = CompressResolutionOptions[0]; // Original
        _selectedCompressFps = CompressFpsOptions[0]; // source rate
        _selectedCompressAudioBitrate = CompressAudioBitrateOptions[0]; // Auto
        _selectedCompressAudioChannels = CompressAudioChannelsOptions[0]; // Stereo

        VideoCodec initialCodec = RecordingSettings.VideoCodec;
        _selectedCompressCodecOption = Array.Find(CompressCodecOptions, o => o.Value == initialCodec)
            ?? Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H264);

        CompressStatusText = LocalizationService.Instance["CompressStatusReady"];

        var notBusy = this.WhenAnyValue(x => x.IsCompressing, busy => !busy);

        PickCompressInputCommand = ReactiveCommand.CreateFromTask(PickCompressInputAsync, notBusy);

        // Output picker needs a source chosen (to seed the suggested name) and no encode in flight.
        PickCompressOutputCommand = ReactiveCommand.CreateFromTask(
            PickCompressOutputAsync,
            this.WhenAnyValue(
                x => x.CompressInputPath,
                x => x.IsCompressing,
                (path, busy) => !string.IsNullOrEmpty(path) && !busy));

        var canCompress = this.WhenAnyValue(
            x => x.CompressInputPath,
            x => x.IsCompressing,
            (path, busy) => !string.IsNullOrEmpty(path) && !busy);
        CompressCommand = ReactiveCommand.CreateFromTask(CompressAsync, canCompress);

        // Cancel is only meaningful while an encode is running.
        CancelCompressCommand = ReactiveCommand.Create(CancelCompress, this.WhenAnyValue(x => x.IsCompressing));

        // Pause only while running and not already paused; Resume only while running and paused.
        PauseCompressCommand = ReactiveCommand.Create(
            PauseCompress,
            this.WhenAnyValue(x => x.IsCompressing, x => x.IsPaused, (busy, paused) => busy && !paused));
        ResumeCompressCommand = ReactiveCommand.Create(
            ResumeCompress,
            this.WhenAnyValue(x => x.IsCompressing, x => x.IsPaused, (busy, paused) => busy && paused));

        // Recompute the live size estimate whenever a relevant knob changes (source change calls it directly).
        this.WhenAnyValue(
                x => x.SelectedCompressCodecOption,
                x => x.SelectedCompressResolution,
                x => x.SelectedCompressFps,
                x => x.CompressCrf,
                x => x.CompressUseTargetSize,
                x => x.CompressTargetSizeMB,
                x => x.CompressDropAudio,
                x => x.SelectedCompressAudioBitrate,
                (a, b, c, d, e, f, g, h) => Unit.Default)
            .Subscribe(_ => UpdateEstimate());

        // Keep a custom output path's extension in sync if the user later changes the output format.
        this.WhenAnyValue(x => x.SelectedCompressFormat).Subscribe(fmt =>
        {
            if (!string.IsNullOrWhiteSpace(CompressOutputPath) && !string.IsNullOrWhiteSpace(fmt))
            {
                CompressOutputPath = Path.ChangeExtension(CompressOutputPath, "." + fmt.ToLowerInvariant());
            }
        });

        PickCompressInputCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Pick", ex));
        PickCompressOutputCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.PickOutput", ex));
        CompressCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Run", ex));
        CancelCompressCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Cancel", ex));
        PauseCompressCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Pause", ex));
        ResumeCompressCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.Resume", ex));
    }

    private void CancelCompress()
    {
        if (_compressCts is { IsCancellationRequested: false })
        {
            CompressStatusText = LocalizationService.Instance["StatusCompressCancelling"];
            // Release the pause gate first so a paused encode wakes and then observes cancellation.
            _compressPauseGate?.Set();
            IsPaused = false;
            _compressCts.Cancel();
        }
    }

    private void PauseCompress()
    {
        if (IsCompressing && !IsPaused && _compressPauseGate != null)
        {
            _compressPauseGate.Reset(); // encode loop blocks at the next frame boundary
            IsPaused = true;
            CompressStatusText = LocalizationService.Instance["StatusCompressPaused"];
        }
    }

    private void ResumeCompress()
    {
        if (IsCompressing && IsPaused && _compressPauseGate != null)
        {
            _compressPauseGate.Set(); // unblock the encode loop
            IsPaused = false;
            CompressStatusText = LocalizationService.Instance["StatusCompressing"];
        }
    }

    private async Task PickCompressOutputAsync()
    {
        if (PickCompressOutputAction == null || string.IsNullOrEmpty(CompressInputPath))
        {
            return;
        }

        string ext = "." + SelectedCompressFormat.ToLowerInvariant();
        string suggested = string.IsNullOrWhiteSpace(CompressOutputPath)
            ? BuildCompressOutputPath(CompressInputPath, ext)
            : CompressOutputPath;

        string? chosen = await PickCompressOutputAction(suggested);
        if (string.IsNullOrEmpty(chosen))
        {
            return;
        }

        CompressOutputPath = chosen;

        // Mirror the chosen extension back into the format combo when it maps to a supported container.
        string chosenExt = Path.GetExtension(chosen).TrimStart('.');
        string? match = Array.Find(CompressOutputFormats, f => f.Equals(chosenExt, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            SelectedCompressFormat = match;
        }
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
        CompressOutputPath = string.Empty; // a path tied to the previous source is stale; revert to auto
        await ProbeSourceAsync(path);
        CompressInputInfo = BuildInputInfo(path);
        CompressStatusText = LocalizationService.Instance["CompressStatusReady"];
        UpdateEstimate();
    }

    // Probes the source's resolution / fps / duration into the _source* fields for the size estimate.
    private async Task ProbeSourceAsync(string path)
    {
        _sourceWidth = 0;
        _sourceHeight = 0;
        _sourceFps = 0;
        _sourceDurationSeconds = 0;
        try
        {
            using var probe = new LibavVideoFramePlayer();
            _sourceDurationSeconds = await probe.ProbeDurationSecondsAsync(path) ?? 0;
            var size = await probe.ProbeVideoSizeAsync(path);
            if (size is { } s)
            {
                _sourceWidth = s.Width;
                _sourceHeight = s.Height;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeSource", ex);
        }

        try
        {
            _sourceFps = await Task.Run(() => LibavClipExporter.ProbeFps(path));
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeFps", ex);
        }
    }

    private string BuildInputInfo(string path)
    {
        string name = Path.GetFileName(path);
        string size = FormatFileSize(new FileInfo(path).Length);
        string duration = _sourceDurationSeconds > 0
            ? TimeSpan.FromSeconds(_sourceDurationSeconds).ToString(@"hh\:mm\:ss")
            : "--:--";
        return $"{name}  ·  {duration}  ·  {size}";
    }

    // Recomputes the live "estimated output size" text from the current settings + probed source metadata.
    private void UpdateEstimate()
    {
        if (_sourceDurationSeconds <= 0 || _sourceWidth <= 0 || _sourceHeight <= 0)
        {
            CompressEstimateText = string.Empty;
            return;
        }

        string prefix = LocalizationService.Instance["CompressEstimateLabel"];

        if (CompressUseTargetSize)
        {
            // Target-size mode encodes to (approximately) the requested size by design.
            CompressEstimateText = $"{prefix}: ≈ {FormatFileSize((long)((double)CompressTargetSizeMB * 1024 * 1024))}";
            return;
        }

        VideoCodec codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264;
        int maxHeight = SelectedCompressResolution?.MaxHeight ?? 0;
        int maxFps = SelectedCompressFps?.Fps ?? 0;
        int crf = Math.Clamp(CompressCrf, 1, 51);
        int audioKbps = CompressDropAudio
            ? 0
            : (SelectedCompressAudioBitrate?.Kbps > 0 ? SelectedCompressAudioBitrate.Kbps : 128);

        long est = EstimateOutputSizeBytes(
            _sourceWidth, _sourceHeight, _sourceFps, _sourceDurationSeconds, maxHeight, maxFps, codec, crf, audioKbps);
        CompressEstimateText = $"{prefix}: ≈ {FormatFileSize(est)}";
    }

    /// <summary>
    /// Rough output-size estimate (bytes) for CRF mode from source geometry/duration and the chosen knobs.
    /// Uses a bits-per-pixel model (≈ halves every +6 CRF; H.265 ≈ 0.6× H.264) so it is approximate and
    /// content-agnostic — directionally correct for comparing settings, not a guarantee. Pure/testable.
    /// </summary>
    internal static long EstimateOutputSizeBytes(
        int srcWidth, int srcHeight, int srcFps, double durationSeconds,
        int maxHeight, int maxFps, VideoCodec codec, int crf, int audioKbps)
    {
        if (durationSeconds <= 0 || srcWidth <= 0 || srcHeight <= 0)
        {
            return 0;
        }

        (int w, int h) = LibavClipExporter.ScaleToMaxHeight(srcWidth, srcHeight, maxHeight);
        int baseFps = srcFps > 0 ? srcFps : 30;
        int fps = (maxFps > 0 && maxFps < baseFps) ? maxFps : baseFps;

        double bppRef = codec == VideoCodec.H265 ? 0.050 : 0.085; // bits/pixel at CRF 23
        double bpp = bppRef * Math.Pow(2.0, -(crf - 23) / 6.0);
        double videoBytes = (double)w * h * fps * bpp * durationSeconds / 8.0;
        double audioBytes = audioKbps > 0 ? audioKbps * 1000.0 / 8.0 * durationSeconds : 0;
        return (long)(videoBytes + audioBytes);
    }

    private async Task CompressAsync()
    {
        if (IsCompressing || string.IsNullOrEmpty(CompressInputPath) || !File.Exists(CompressInputPath))
        {
            return;
        }

        // Output path: a custom path (from the Save dialog) wins; otherwise auto-derive next to the source
        // ("<name>_compressed.<ext>", de-duplicated) so a single "Compress" click just runs.
        string outputPath;
        if (!string.IsNullOrWhiteSpace(CompressOutputPath))
        {
            outputPath = CompressOutputPath;
            // Guard against overwriting the source itself with a same-path custom selection.
            if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(CompressInputPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                CompressStatusText = LocalizationService.Instance["StatusCompressFailed"];
                ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Error);
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.PrepareOutputDir", ex);
            }
        }
        else
        {
            string ext = "." + SelectedCompressFormat.ToLowerInvariant();
            outputPath = BuildCompressOutputPath(CompressInputPath, ext);
        }

        VideoCodec codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264;
        int crf = Math.Clamp(CompressCrf, 1, 51);
        int maxHeight = SelectedCompressResolution?.MaxHeight ?? 0;
        int maxFps = SelectedCompressFps?.Fps ?? 0;
        string preset = SelectedCompressPreset;
        bool dropAudio = CompressDropAudio;
        int audioBitrateKbps = SelectedCompressAudioBitrate?.Kbps ?? 0;
        int audioChannels = SelectedCompressAudioChannels?.Channels ?? 0;
        string input = CompressInputPath;

        // True two-pass replaces the adaptive corrective pass for H.264 target-size (more accurate, better
        // quality distribution). H.265 target-size keeps single-pass ABR + the corrective re-encode below.
        bool useTwoPass = CompressUseTargetSize && codec == VideoCodec.H264;

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
        CompressProgress = 0;
        CompressStatusText = LocalizationService.Instance["StatusCompressing"];

        _compressCts?.Dispose();
        _compressCts = new CancellationTokenSource();
        CancellationToken token = _compressCts.Token;

        _compressPauseGate?.Dispose();
        _compressPauseGate = new ManualResetEventSlim(true); // starts signaled = running
        IsPaused = false;

        // Marshals exporter progress back to the UI thread (Progress<T> captures this sync context).
        var encodeProgress = new Progress<double>(p => CompressProgress = p);

        string outExt = Path.GetExtension(outputPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 0 duration (unreadable) falls back to a large-but-sane end (24h) so the whole file is
            // exported without risking an oversized duration-derived buffer inside the exporter.
            double end = probedDuration > 0 ? probedDuration : 24 * 60 * 60;
            var ranges = new[] { new LibavClipExporter.SourceRange(0, end) };

            Directory.CreateDirectory(tempDir);

            // Encodes at a given bitrate (0 = quality CRF) into a uniquely-named temp; returns its path or null.
            async Task<string?> EncodeAttemptAsync(int bitrateKbps, int attempt)
            {
                string attemptOut = Path.Combine(tempDir, $"attempt{attempt}{outExt}");
                var options = new LibavExportOptions
                {
                    Codec = codec,
                    TargetVideoBitrateKbps = bitrateKbps,
                    CrfOverride = crf,
                    Preset = preset,
                    MaxHeight = maxHeight,
                    MaxFps = maxFps,
                    DropAudio = dropAudio,
                    AudioBitrateKbps = audioBitrateKbps,
                    AudioChannels = audioChannels,
                    TwoPass = useTwoPass
                };
                // A corrective second pass restarts progress from 0 (status text signals the refine phase).
                if (attempt > 1)
                {
                    CompressProgress = 0;
                }
                bool encoded = await Task.Run(() =>
                    LibavClipExporter.TryExport(
                        input, ranges, attemptOut, VideoQuality.Medium,
                        cancellationToken: token, options: options, progress: encodeProgress,
                        pauseGate: _compressPauseGate), token);
                return encoded && File.Exists(attemptOut) && new FileInfo(attemptOut).Length > 0
                    ? attemptOut
                    : null;
            }

            string? finalTemp = await EncodeAttemptAsync(targetBitrateKbps, 1);

            // Target-size accuracy for the SINGLE-PASS path (H.265): single-pass ABR can overshoot, so if the
            // first attempt exceeds the requested size, re-encode once at a proportionally lower bitrate
            // (audio held constant). H.264 uses true two-pass above and needs no corrective re-encode.
            if (finalTemp != null && CompressUseTargetSize && !useTwoPass)
            {
                double targetBytes = (double)CompressTargetSizeMB * 1024 * 1024;
                long actualBytes = new FileInfo(finalTemp).Length;
                if (actualBytes > targetBytes)
                {
                    int refined = RefineTargetVideoBitrateKbps(targetBitrateKbps, actualBytes, targetBytes, probedDuration);
                    if (refined > 0 && refined < targetBitrateKbps)
                    {
                        CompressStatusText = LocalizationService.Instance["StatusCompressRefining"];
                        string? secondTemp = await EncodeAttemptAsync(refined, 2);
                        // Keep the corrective pass only if it actually came in smaller (and still valid).
                        if (secondTemp != null && new FileInfo(secondTemp).Length < actualBytes)
                        {
                            finalTemp = secondTemp;
                        }
                    }
                }
            }

            if (finalTemp != null)
            {
                File.Copy(finalTemp, outputPath, true);

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
        catch (OperationCanceledException)
        {
            // User cancelled: leave no partial output (the temp dir is wiped in finally; outputPath was
            // never written because the copy only runs on a completed encode).
            CompressStatusText = LocalizationService.Instance["StatusCompressCancelled"];
            ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Info);
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
            IsPaused = false;
            _compressCts?.Dispose();
            _compressCts = null;
            _compressPauseGate?.Dispose();
            _compressPauseGate = null;
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

    /// <summary>
    /// After a first ABR pass overshot the target, scales the video bitrate by the measured
    /// video-bytes ratio (audio held at a fixed allowance) so a single corrective re-encode lands at or
    /// under the requested size. Returns the new kbps, floored, or the original if inputs are unusable.
    /// </summary>
    internal static int RefineTargetVideoBitrateKbps(
        int attemptedVideoKbps, double actualTotalBytes, double targetTotalBytes, double durationSeconds)
    {
        if (attemptedVideoKbps <= 0 || actualTotalBytes <= 0 || targetTotalBytes <= 0 || durationSeconds <= 0)
        {
            return attemptedVideoKbps;
        }

        const double audioKbps = 128;   // matches the reservation in ComputeTargetVideoBitrateKbps
        const double safety = 0.98;     // small extra headroom so the corrective pass stays under target

        double audioBytes = audioKbps * 1000.0 / 8.0 * durationSeconds;
        double actualVideoBytes = Math.Max(1, actualTotalBytes - audioBytes);
        double targetVideoBytes = Math.Max(1, targetTotalBytes - audioBytes);

        double refined = attemptedVideoKbps * (targetVideoBytes / actualVideoBytes) * safety;
        return (int)Math.Max(50, Math.Floor(refined));
    }

    /// <summary>
    /// Builds the output path next to the source as "&lt;name&gt;_compressed&lt;ext&gt;", appending " (n)" until
    /// the name is free so it never overwrites the source or a previous compress.
    /// </summary>
    private static string BuildCompressOutputPath(string inputPath, string ext)
    {
        string dir = Path.GetDirectoryName(inputPath) ?? Path.GetTempPath();
        string baseName = Path.GetFileNameWithoutExtension(inputPath) + "_compressed";

        string candidate = Path.Combine(dir, baseName + ext);
        for (int n = 1; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(dir, $"{baseName} ({n}){ext}");
        }

        return candidate;
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
