using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    /// <summary>Set by the view: opens a folder picker for the batch output folder; returns the chosen dir (or null).</summary>
    public Func<Task<string?>>? PickCompressOutputFolderAction { get; set; }

    /// <summary>Set by the view: opens the side-by-side quality-compare window for a prepared view model.</summary>
    internal Action<CompareViewModel>? OpenCompareAction { get; set; }

    public ReactiveCommand<Unit, Unit> CompareQualityCommand { get; private set; } = null!;

    // Optional batch output folder. Empty = auto-save each output next to its source.
    private string _compressOutputFolder = string.Empty;
    public string CompressOutputFolder
    {
        get => _compressOutputFolder;
        set => this.RaiseAndSetIfChanged(ref _compressOutputFolder, value);
    }

    // Append a "_yyyyMMdd_HHmmss" timestamp to each output file name.
    private bool _compressAppendDate = true;
    public bool CompressAppendDate
    {
        get => _compressAppendDate;
        set => this.RaiseAndSetIfChanged(ref _compressAppendDate, value);
    }

    private string _compressStatusText = string.Empty;
    public string CompressStatusText
    {
        get => _compressStatusText;
        set => this.RaiseAndSetIfChanged(ref _compressStatusText, value);
    }

    // Quality is controlled by a CRF slider (lower = better quality / larger file). 18-28 is the useful
    // range; 23 is a sensible default. Ignored in target-size mode (bitrate drives quality there).
    private int _compressCrf = 22;
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

    private string _selectedCompressPreset = "medium";
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

    // Nullable so clearing the NumericUpDown (which yields null) can't crash the binding; reads coalesce to 25.
    private decimal? _compressTargetSizeMB = 25m;
    public decimal? CompressTargetSizeMB
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

    public ReactiveCommand<Unit, Unit> PickCompressOutputFolderCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearCompressOutputFolderCommand { get; private set; } = null!;

    // Saved setting bundles (persisted JSON). Distinct from SelectedCompressPreset (the encoder speed preset).
    public ObservableCollection<CompressPreset> CompressPresets { get; } = new();

    private CompressPreset? _selectedSavedPreset;
    public CompressPreset? SelectedSavedPreset
    {
        get => _selectedSavedPreset;
        set => this.RaiseAndSetIfChanged(ref _selectedSavedPreset, value);
    }

    private string _compressPresetName = string.Empty;
    public string CompressPresetName
    {
        get => _compressPresetName;
        set => this.RaiseAndSetIfChanged(ref _compressPresetName, value);
    }

    public ReactiveCommand<Unit, Unit> SaveCompressPresetCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> LoadCompressPresetCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteCompressPresetCommand { get; private set; } = null!;

    private void InitializeVideoCompress()
    {
        _selectedCompressResolution = CompressResolutionOptions[0]; // Original
        _selectedCompressFps = CompressFpsOptions[0]; // source rate
        _selectedCompressAudioBitrate = CompressAudioBitrateOptions[0]; // Auto
        _selectedCompressAudioChannels = CompressAudioChannelsOptions[0]; // Stereo

        // Default to the HandBrake "shrink ~80%, near-lossless" recipe: H.265 (with CRF 22 / medium above).
        _selectedCompressCodecOption = Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H265)
            ?? Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H264);

        CompressStatusText = LocalizationService.Instance["CompressStatusReady"];

        var notBusy = this.WhenAnyValue(x => x.IsBusy, busy => !busy);

        // Output folder: an optional batch destination. Pickable when nothing is running; clear to revert
        // to auto-save-next-to-source.
        PickCompressOutputFolderCommand = ReactiveCommand.CreateFromTask(PickCompressOutputFolderAsync, notBusy);
        ClearCompressOutputFolderCommand = ReactiveCommand.Create(
            () => { CompressOutputFolder = string.Empty; },
            this.WhenAnyValue(x => x.CompressOutputFolder, x => x.IsBusy,
                (folder, busy) => !string.IsNullOrEmpty(folder) && !busy));

        // Quality compare: needs a selected file and an idle queue (it encodes a short sample).
        CompareQualityCommand = ReactiveCommand.Create(
            OpenCompare,
            this.WhenAnyValue(x => x.SelectedQueueItem, x => x.IsBusy,
                (CompressQueueItem? sel, bool busy) => sel != null && !busy));
        CompareQualityCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.OpenCompare", ex));

        // Presets: save needs a non-blank name; load/delete need a selection. None while busy.
        SaveCompressPresetCommand = ReactiveCommand.Create(
            SaveCompressPreset,
            this.WhenAnyValue(x => x.CompressPresetName, x => x.IsBusy,
                (name, busy) => !string.IsNullOrWhiteSpace(name) && !busy));
        LoadCompressPresetCommand = ReactiveCommand.Create(
            LoadCompressPreset,
            this.WhenAnyValue(x => x.SelectedSavedPreset, x => x.IsBusy,
                (sel, busy) => sel != null && !busy));
        DeleteCompressPresetCommand = ReactiveCommand.Create(
            DeleteCompressPreset,
            this.WhenAnyValue(x => x.SelectedSavedPreset, x => x.IsBusy,
                (sel, busy) => sel != null && !busy));

        foreach (CompressPreset preset in CompressPresetService.Load())
        {
            CompressPresets.Add(preset);
        }

        // Restore last-used settings before the queue is populated, so estimates use them from the start.
        RestoreCompressSession();

        InitializeCompressBatch();

        // Recompute every queued file's size estimate whenever a relevant output setting changes.
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
            .Subscribe(_ => RecomputeQueueEstimates());

        // Persist settings on any change so they (and a resumed batch) survive a restart (no-op during restore).
        this.WhenAnyValue(
                x => x.SelectedCompressCodecOption, x => x.SelectedCompressResolution, x => x.SelectedCompressFps,
                x => x.SelectedCompressPreset, x => x.SelectedCompressFormat, x => x.CompressCrf,
                x => x.CompressUseTargetSize, x => x.CompressTargetSizeMB, x => x.CompressDropAudio,
                x => x.SelectedCompressAudioBitrate, x => x.SelectedCompressAudioChannels,
                (a, b, c, d, e, f, g, h, i, j, k) => Unit.Default)
            .Skip(1)
            .Subscribe(_ => SaveCompressSession());
        this.WhenAnyValue(
                x => x.CompressOutputFolder, x => x.CompressAppendDate, x => x.CompressParallelCount,
                (a, b, c) => Unit.Default)
            .Skip(1)
            .Subscribe(_ => SaveCompressSession());

        PickCompressOutputFolderCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.PickOutputFolder", ex));
        ClearCompressOutputFolderCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.ClearOutputFolder", ex));
        SaveCompressPresetCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.PresetSave", ex));
        LoadCompressPresetCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.PresetLoad", ex));
        DeleteCompressPresetCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.PresetDelete", ex));
    }

    private async Task PickCompressOutputFolderAsync()
    {
        if (PickCompressOutputFolderAction == null)
        {
            return;
        }

        string? folder = await PickCompressOutputFolderAction();
        if (!string.IsNullOrEmpty(folder))
        {
            CompressOutputFolder = folder;
        }
    }

    // Snapshots the current Compress-tab settings into a named preset.
    private CompressPreset CaptureCurrentPreset(string name) => new()
    {
        Name = name,
        Codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264,
        MaxHeight = SelectedCompressResolution?.MaxHeight ?? 0,
        MaxFps = SelectedCompressFps?.Fps ?? 0,
        Preset = SelectedCompressPreset,
        Format = SelectedCompressFormat,
        Crf = CompressCrf,
        UseTargetSize = CompressUseTargetSize,
        TargetSizeMB = CompressTargetSizeMB ?? 25m,
        DropAudio = CompressDropAudio,
        AudioBitrateKbps = SelectedCompressAudioBitrate?.Kbps ?? 0,
        AudioChannels = SelectedCompressAudioChannels?.Channels ?? 2
    };

    private void SaveCompressPreset()
    {
        string name = (CompressPresetName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return;
        }

        CompressPreset preset = CaptureCurrentPreset(name);
        // Overwrite a same-named preset (case-insensitive) rather than duplicating it.
        CompressPreset? existing = CompressPresets.FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            CompressPresets.Remove(existing);
        }

        CompressPresets.Add(preset);
        CompressPresetService.Save(CompressPresets);
        SelectedSavedPreset = preset;
        ShowToastAction?.Invoke(LocalizationService.Instance["CompressPresetSaved"], ToastSeverity.Success);
    }

    private void LoadCompressPreset()
    {
        CompressPreset? p = SelectedSavedPreset;
        if (p == null)
        {
            return;
        }

        ApplyCompressPreset(p);
        CompressPresetName = p.Name; // so a follow-up Save updates this preset
        // RecomputeQueueEstimates fires via the WhenAnyValue subscriptions as the properties above change.
    }

    // Applies a preset's encode settings to the Compress-tab controls (shared by load-preset and session restore).
    private void ApplyCompressPreset(CompressPreset p)
    {
        SelectedCompressCodecOption = Array.Find(CompressCodecOptions, o => o.Value == p.Codec)
            ?? Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H264);
        SelectedCompressResolution = Array.Find(CompressResolutionOptions, o => o.MaxHeight == p.MaxHeight)
            ?? CompressResolutionOptions[0];
        SelectedCompressFps = Array.Find(CompressFpsOptions, o => o.Fps == p.MaxFps) ?? CompressFpsOptions[0];
        SelectedCompressPreset = CompressPresetOptions.Contains(p.Preset) ? p.Preset : "veryfast";
        SelectedCompressFormat = CompressOutputFormats.Contains(p.Format) ? p.Format : "MP4";
        CompressCrf = Math.Clamp(p.Crf, 14, 40);
        CompressUseTargetSize = p.UseTargetSize;
        CompressTargetSizeMB = p.TargetSizeMB > 0 ? p.TargetSizeMB : 25m;
        CompressDropAudio = p.DropAudio;
        SelectedCompressAudioBitrate = Array.Find(CompressAudioBitrateOptions, o => o.Kbps == p.AudioBitrateKbps)
            ?? CompressAudioBitrateOptions[0];
        SelectedCompressAudioChannels = Array.Find(CompressAudioChannelsOptions, o => o.Channels == p.AudioChannels)
            ?? CompressAudioChannelsOptions[0];
    }

    private bool _restoringSession;

    // Restores the last-used Compress settings (encode + batch options) saved from a previous run.
    // Bump when the built-in default encode settings change and existing users should be upgraded once.
    private const int CompressDefaultsVersion = 1;

    private void RestoreCompressSession()
    {
        CompressSessionState session = CompressSessionService.Load();
        // One-time upgrade: a session from before the current defaults keeps the new code defaults (skip
        // applying its saved encode settings); batch options are still restored.
        bool upgrade = session.Version < CompressDefaultsVersion;
        _restoringSession = true;
        try
        {
            if (!upgrade)
            {
                ApplyCompressPreset(session.Settings);
            }
            CompressOutputFolder = session.OutputFolder ?? string.Empty;
            CompressAppendDate = session.AppendDate;
            CompressParallelCount = CompressParallelOptions.Contains(session.ParallelCount)
                ? session.ParallelCount
                : CompressParallelCount;
        }
        finally
        {
            _restoringSession = false;
        }

        if (upgrade)
        {
            SaveCompressSession(); // persist the new defaults + version so the upgrade happens only once
        }
    }

    // Persists the current Compress settings so they survive a restart (no-op during restore).
    private void SaveCompressSession()
    {
        if (_restoringSession)
        {
            return;
        }

        CompressSessionService.Save(new CompressSessionState
        {
            Settings = CaptureCurrentPreset(string.Empty),
            OutputFolder = CompressOutputFolder,
            AppendDate = CompressAppendDate,
            ParallelCount = CompressParallelCount,
            Version = CompressDefaultsVersion
        });
    }

    private void DeleteCompressPreset()
    {
        CompressPreset? p = SelectedSavedPreset;
        if (p == null)
        {
            return;
        }

        CompressPresets.Remove(p);
        CompressPresetService.Save(CompressPresets);
        SelectedSavedPreset = null;
    }

    // Recomputes every queued file's "≈ size" estimate from its probed metadata + the current settings.
    private void RecomputeQueueEstimates()
    {
        foreach (CompressQueueItem item in CompressQueue)
        {
            item.EstimatedText = BuildItemEstimate(item);
        }
    }

    private string BuildItemEstimate(CompressQueueItem item)
    {
        if (item.ProbedDuration <= 0 || item.ProbedWidth <= 0 || item.ProbedHeight <= 0)
        {
            return string.Empty; // not probed yet (or unreadable)
        }

        string prefix = LocalizationService.Instance["CompressEstimateLabel"];

        if (CompressUseTargetSize)
        {
            // Target-size mode encodes to (approximately) the requested size by design.
            return $"{prefix}: ≈ {FormatFileSize((long)((double)(CompressTargetSizeMB ?? 25m) * 1024 * 1024))}";
        }

        VideoCodec codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264;
        int maxHeight = SelectedCompressResolution?.MaxHeight ?? 0;
        int maxFps = SelectedCompressFps?.Fps ?? 0;
        int crf = Math.Clamp(CompressCrf, 1, 51);
        int audioKbps = CompressDropAudio
            ? 0
            : (SelectedCompressAudioBitrate?.Kbps > 0 ? SelectedCompressAudioBitrate.Kbps : 128);

        long est = EstimateOutputSizeBytes(
            item.ProbedWidth, item.ProbedHeight, item.ProbedFps, item.ProbedDuration, maxHeight, maxFps, codec, crf, audioKbps);
        return $"{prefix}: ≈ {FormatFileSize(est)}";
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

    /// <summary>
    /// Accurate output-size estimate (bytes) for CRF mode: encodes a few short windows of the source at the
    /// snapshot settings, measures the real bytes, and extrapolates to the full duration. Returns -1 on
    /// failure. Far better than the formula because it sees the actual footage. Runs the encode off-thread.
    /// </summary>
    private async Task<long> EstimateBySampleAsync(string sourcePath, CompressSettingsSnapshot snap, double duration)
    {
        if (duration <= 0)
        {
            return -1;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Estimate_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string sampleOut = Path.Combine(tempDir, "sample.mp4");
            LibavClipExporter.SourceRange[] ranges = BuildSampleRanges(duration, out double sampleDuration);
            if (sampleDuration <= 0)
            {
                return -1;
            }

            // CRF sample (no target bitrate / two-pass): same knobs that drive the real per-file size.
            var options = new LibavExportOptions
            {
                Codec = snap.Codec,
                CrfOverride = snap.Crf,
                Preset = snap.Preset,
                MaxHeight = snap.MaxHeight,
                MaxFps = snap.MaxFps,
                DropAudio = snap.DropAudio,
                AudioBitrateKbps = snap.AudioBitrateKbps,
                AudioChannels = snap.AudioChannels
            };

            bool ok = await Task.Run(() =>
                LibavClipExporter.TryExport(sourcePath, ranges, sampleOut, VideoQuality.Medium, options: options));
            if (!ok || !File.Exists(sampleOut))
            {
                return -1;
            }

            long sampleBytes = new FileInfo(sampleOut).Length;
            return (long)(sampleBytes * (duration / sampleDuration));
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.EstimateSample", ex);
            return -1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.EstimateCleanup", ex);
            }
        }
    }

    // Short clips (<= 8s) sample whole (so the estimate is exact). Longer ones take three 2s windows at
    // 20/50/80% so the measured bitrate reflects content variation across the file.
    private static LibavClipExporter.SourceRange[] BuildSampleRanges(double duration, out double sampleDuration)
    {
        if (duration <= 8)
        {
            sampleDuration = duration;
            return new[] { new LibavClipExporter.SourceRange(0, duration) };
        }

        const double window = 2.0;
        var ranges = new List<LibavClipExporter.SourceRange>();
        double total = 0;
        foreach (double start in new[] { duration * 0.2, duration * 0.5, duration * 0.8 })
        {
            double end = Math.Min(duration, start + window);
            if (end > start)
            {
                ranges.Add(new LibavClipExporter.SourceRange(start, end));
                total += end - start;
            }
        }

        sampleDuration = total;
        return ranges.ToArray();
    }

    // Immutable snapshot of the encode knobs so a run reads stable values (a run reads these off the UI thread).
    private readonly record struct CompressSettingsSnapshot(
        VideoCodec Codec, int Crf, int MaxHeight, int MaxFps, string Preset,
        bool DropAudio, int AudioBitrateKbps, int AudioChannels,
        bool UseTargetSize, decimal TargetSizeMB, bool UseTwoPass);

    private CompressSettingsSnapshot BuildSettingsSnapshot()
    {
        VideoCodec codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264;
        return new CompressSettingsSnapshot(
            codec,
            Math.Clamp(CompressCrf, 1, 51),
            SelectedCompressResolution?.MaxHeight ?? 0,
            SelectedCompressFps?.Fps ?? 0,
            SelectedCompressPreset,
            CompressDropAudio,
            SelectedCompressAudioBitrate?.Kbps ?? 0,
            SelectedCompressAudioChannels?.Channels ?? 0,
            CompressUseTargetSize,
            CompressTargetSizeMB ?? 25m,
            // True two-pass replaces the corrective pass for H.264 target-size; H.265 keeps single-pass + correct.
            CompressUseTargetSize && codec == VideoCodec.H264);
    }

    private static async Task<double> ProbeInputDurationAsync(string input)
    {
        try
        {
            using var probe = new LibavVideoFramePlayer();
            return await probe.ProbeDurationSecondsAsync(input) ?? 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeDuration", ex);
            return 0;
        }
    }

    /// <summary>
    /// Encodes one input to outputPath with the snapshot settings (incl. the H.265 single-pass corrective
    /// re-encode). Returns true on success; throws <see cref="OperationCanceledException"/> if cancelled.
    /// Manages its own temp dir and sets no shared UI state, so single + batch runs share one code path.
    /// </summary>
    private async Task<bool> EncodeOneFileAsync(
        string input, string outputPath, CompressSettingsSnapshot s, int targetBitrateKbps,
        double durationSeconds, IProgress<double> progress, CancellationToken token, ManualResetEventSlim? pauseGate,
        int rotationDegrees = 0, IProgress<double>? resumeProgress = null)
    {
        // CRF mode with a known duration uses the resumable segmented path; target-size / 2-pass and the
        // unknown-duration fallback keep the whole-file path below (their size budget needs the whole file).
        if (!s.UseTargetSize && durationSeconds > 0)
        {
            return await EncodeOneFileSegmentedAsync(
                input, outputPath, s, durationSeconds, progress, token, pauseGate, rotationDegrees, resumeProgress);
        }

        string outExt = Path.GetExtension(outputPath);
        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 0 duration (unreadable) falls back to a large-but-sane end so the whole file is exported.
            double end = durationSeconds > 0 ? durationSeconds : 24 * 60 * 60;
            var ranges = new[] { new LibavClipExporter.SourceRange(0, end) };
            Directory.CreateDirectory(tempDir);

            async Task<string?> EncodeAttemptAsync(int bitrateKbps, int attempt)
            {
                string attemptOut = Path.Combine(tempDir, $"attempt{attempt}{outExt}");
                var options = new LibavExportOptions
                {
                    Codec = s.Codec,
                    TargetVideoBitrateKbps = bitrateKbps,
                    CrfOverride = s.Crf,
                    Preset = s.Preset,
                    MaxHeight = s.MaxHeight,
                    MaxFps = s.MaxFps,
                    DropAudio = s.DropAudio,
                    AudioBitrateKbps = s.AudioBitrateKbps,
                    AudioChannels = s.AudioChannels,
                    TwoPass = s.UseTwoPass,
                    RotationDegrees = rotationDegrees
                };
                if (attempt > 1)
                {
                    progress.Report(0); // corrective pass restarts the bar
                }
                bool encoded = await Task.Run(() =>
                    LibavClipExporter.TryExport(
                        input, ranges, attemptOut, VideoQuality.Medium,
                        cancellationToken: token, options: options, progress: progress, pauseGate: pauseGate), token);
                return encoded && File.Exists(attemptOut) && new FileInfo(attemptOut).Length > 0 ? attemptOut : null;
            }

            string? finalTemp = await EncodeAttemptAsync(targetBitrateKbps, 1);

            // H.265 single-pass corrective re-encode if the first attempt overshot the target.
            if (finalTemp != null && s.UseTargetSize && !s.UseTwoPass)
            {
                double targetBytes = (double)s.TargetSizeMB * 1024 * 1024;
                long actualBytes = new FileInfo(finalTemp).Length;
                if (actualBytes > targetBytes)
                {
                    int refined = RefineTargetVideoBitrateKbps(targetBitrateKbps, actualBytes, targetBytes, durationSeconds);
                    if (refined > 0 && refined < targetBitrateKbps)
                    {
                        string? secondTemp = await EncodeAttemptAsync(refined, 2);
                        if (secondTemp != null && new FileInfo(secondTemp).Length < actualBytes)
                        {
                            finalTemp = secondTemp;
                        }
                    }
                }
            }

            if (finalTemp == null)
            {
                return false;
            }

            File.Copy(finalTemp, outputPath, true);
            return true;
        }
        finally
        {
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

    // ~30s chunks: small enough that an interruption loses little, large enough to keep keyframe/file overhead low.
    private const double CompressChunkSeconds = 30.0;

    /// <summary>
    /// CRF-mode encode that resumes across restarts: encodes the source in <see cref="CompressChunkSeconds"/>
    /// video-only chunks, persisting which are done, then concatenates them and muxes audio once. On resume it
    /// reuses chunks already on disk, so 繼續 continues near the interruption instead of restarting from 0%.
    /// The pause gate / cancellation are honored inside each chunk; partial chunks are kept for resume (the
    /// caller cleans them up only on cancel/success).
    /// </summary>
    private async Task<bool> EncodeOneFileSegmentedAsync(
        string input, string outputPath, CompressSettingsSnapshot s, double durationSeconds,
        IProgress<double> progress, CancellationToken token, ManualResetEventSlim? pauseGate, int rotationDegrees,
        IProgress<double>? resumeProgress = null)
    {
        int chunkCount = Math.Max(1, (int)Math.Ceiling(durationSeconds / CompressChunkSeconds));
        string settingsKey = BuildSegmentSettingsKey(s, rotationDegrees);

        // Load prior resume state; reset it only if the settings / duration no longer match the chunks. The
        // output path is NOT a validity key — it carries a per-run date stamp, so it legitimately differs each
        // session; the chunks are keyed by input + settings and stay reusable regardless of the output name.
        CompressSegmentState? state = CompressSegmentStore.Load(input);
        if (state == null
            || state.SettingsKey != settingsKey
            || Math.Abs(state.TotalDuration - durationSeconds) > 0.5
            || state.ChunkCount != chunkCount)
        {
            CompressSegmentStore.Clear(input);
            state = new CompressSegmentState
            {
                InputPath = input,
                OutputPath = outputPath,
                SettingsKey = settingsKey,
                TotalDuration = durationSeconds,
                ChunkSeconds = CompressChunkSeconds,
                ChunkCount = chunkCount,
                CompletedChunks = new List<int>()
            };
            CompressSegmentStore.Save(state);
        }

        var completed = new HashSet<int>(state.CompletedChunks);
        resumeProgress?.Report((double)completed.Count / chunkCount); // current safe resume checkpoint

        var chunkOptions = new LibavExportOptions
        {
            Codec = s.Codec,
            TargetVideoBitrateKbps = 0,   // CRF
            CrfOverride = s.Crf,
            Preset = s.Preset,
            MaxHeight = s.MaxHeight,
            MaxFps = s.MaxFps,
            DropAudio = true,             // video-only chunks; audio is built once at finalize
            TwoPass = false,
            RotationDegrees = rotationDegrees
        };

        for (int i = 0; i < chunkCount; i++)
        {
            token.ThrowIfCancellationRequested();
            string chunkPath = CompressSegmentStore.ChunkPath(input, i);
            if (completed.Contains(i) && File.Exists(chunkPath) && new FileInfo(chunkPath).Length > 0)
            {
                progress.Report((double)(i + 1) / chunkCount); // already done — advance the bar
                continue;
            }

            double startSec = i * CompressChunkSeconds;
            double endSec = Math.Min((i + 1) * CompressChunkSeconds, durationSeconds);
            var ranges = new[] { new LibavClipExporter.SourceRange(startSec, endSec) };
            int index = i;
            var chunkProgress = new Progress<double>(p => progress.Report((index + Math.Clamp(p, 0, 1)) / chunkCount));

            bool ok = await Task.Run(() => LibavClipExporter.TryExport(
                input, ranges, chunkPath, VideoQuality.Medium,
                cancellationToken: token, options: chunkOptions, progress: chunkProgress, pauseGate: pauseGate), token);
            if (!ok || !File.Exists(chunkPath) || new FileInfo(chunkPath).Length == 0)
            {
                return false;
            }

            completed.Add(i);
            state.CompletedChunks = completed.OrderBy(x => x).ToList();
            CompressSegmentStore.Save(state);
            resumeProgress?.Report((double)completed.Count / chunkCount); // advance the safe checkpoint
        }

        // Finalize: concat the video chunks (lossless), build audio once for the whole file, mux to output.
        token.ThrowIfCancellationRequested();
        string finalizeDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(finalizeDir);
        try
        {
            var chunkPaths = Enumerable.Range(0, chunkCount)
                .Select(i => CompressSegmentStore.ChunkPath(input, i)).ToList();
            if (chunkPaths.Any(p => !File.Exists(p) || new FileInfo(p).Length == 0))
            {
                return false;
            }

            string mergedVideo = Path.Combine(finalizeDir, "merged.mkv");
            bool merged = await Task.Run(() =>
            {
                LibavMuxer.ConcatVideoSegments(chunkPaths, mergedVideo, "matroska");
                return File.Exists(mergedVideo) && new FileInfo(mergedVideo).Length > 0;
            }, token);
            if (!merged)
            {
                return false;
            }

            var audioRanges = new[] { new LibavClipExporter.SourceRange(0, durationSeconds) };
            var muxOptions = new LibavExportOptions
            {
                Codec = s.Codec,
                DropAudio = s.DropAudio,
                AudioBitrateKbps = s.AudioBitrateKbps,
                AudioChannels = s.AudioChannels
            };
            bool finalized = await Task.Run(() => LibavClipExporter.TryMuxAudioForRanges(
                input, audioRanges, mergedVideo, outputPath, VideoQuality.Medium, muxOptions, token), token);
            if (!finalized)
            {
                return false;
            }

            progress.Report(1.0);
            CompressSegmentStore.Clear(input); // success — drop the chunks + state
            return true;
        }
        finally
        {
            try { Directory.Delete(finalizeDir, true); }
            catch (Exception ex) { AppLog.Error("Compress.Cleanup", ex); }
        }
    }

    // Settings the chunks were encoded with; a change invalidates persisted chunks (different pixels/bitstream).
    private static string BuildSegmentSettingsKey(CompressSettingsSnapshot s, int rotationDegrees) =>
        string.Join('|', s.Codec, s.Crf, s.Preset, s.MaxHeight, s.MaxFps,
            s.DropAudio, s.AudioBitrateKbps, s.AudioChannels, rotationDegrees);

    // Toast shown when the user pauses a non-CRF file: only CRF mode can resume mid-encode after a restart.
    private void ShowPauseNoResumeWarning() =>
        ShowToastAction?.Invoke(LocalizationService.Instance["CompressPauseNoResume"], ToastSeverity.Info);

    // Builds a CompareViewModel for the selected file with the current encode settings and hands it to the view.
    private void OpenCompare()
    {
        CompressQueueItem? item = SelectedQueueItem;
        if (item == null || OpenCompareAction == null)
        {
            return;
        }

        CompressSettingsSnapshot snap = BuildSettingsSnapshot();
        int targetKbps = snap.UseTargetSize && item.ProbedDuration > 0
            ? ComputeTargetVideoBitrateKbps((double)snap.TargetSizeMB, item.ProbedDuration)
            : 0;

        var options = new LibavExportOptions
        {
            Codec = snap.Codec,
            TargetVideoBitrateKbps = targetKbps,
            CrfOverride = snap.Crf,
            Preset = snap.Preset,
            MaxHeight = snap.MaxHeight,
            MaxFps = snap.MaxFps,
            DropAudio = snap.DropAudio,
            AudioBitrateKbps = snap.AudioBitrateKbps,
            AudioChannels = snap.AudioChannels,
            RotationDegrees = 0 // rotation is applied at display time to BOTH frames, not baked into the sample
        };

        var vm = new CompareViewModel(
            item.Path, item.ProbedDuration, item.ProbedFps, item.ProbedWidth, item.ProbedHeight, options, item.Rotation);
        OpenCompareAction(vm);
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
    /// Composes a batch output path: base = <paramref name="rootFolder"/> when set, else the source's own
    /// folder. The per-item <paramref name="outputName"/> may carry a relative subfolder path plus a file name
    /// (e.g. <c>\sub\clip</c>); it is sanitized so it can't escape the base (no drive, no <c>..</c>). The name
    /// falls back to the source name when blank, a <c>_yyyyMMdd_HHmmss</c> stamp is appended when
    /// <paramref name="appendDate"/> is set, and " (n)" is added until the name is free.
    /// </summary>
    internal static string BuildBatchOutputPath(
        string sourcePath, string? outputName, string? rootFolder, string ext, bool appendDate, DateTime timestamp)
    {
        string baseDir = !string.IsNullOrWhiteSpace(rootFolder)
            ? rootFolder!
            : (Path.GetDirectoryName(sourcePath) ?? Path.GetTempPath());

        string relDir = string.Empty;
        string namePart = string.Empty;
        string raw = (outputName ?? string.Empty).Trim();
        if (raw.Length > 0)
        {
            string normalized = raw.Replace('/', Path.DirectorySeparatorChar);
            relDir = SanitizeRelativeDir(Path.GetDirectoryName(normalized));
            namePart = SanitizeFileName(Path.GetFileNameWithoutExtension(normalized));
        }
        if (namePart.Length == 0)
        {
            namePart = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        }
        if (namePart.Length == 0)
        {
            namePart = "output";
        }

        string targetDir = relDir.Length > 0 ? Path.Combine(baseDir, relDir) : baseDir;
        string fileBase = appendDate ? $"{namePart}_{timestamp:yyyyMMdd_HHmmss}" : namePart;

        string candidate = Path.Combine(targetDir, fileBase + ext);
        for (int n = 1; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(targetDir, $"{fileBase} ({n}){ext}");
        }

        return candidate;
    }

    // Strips path-invalid characters (and trailing dots) from a single file-name segment.
    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim().Trim('.');
    }

    // Turns a user-typed relative path into safe folder segments: drops ".."/"."/empties + any drive or
    // invalid characters, so the result can never escape the base directory.
    private static string SanitizeRelativeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return string.Empty;
        }

        string[] safe = dir
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p != "." && p != "..")
            .Select(SanitizeFileName)
            .Where(p => p.Length > 0)
            .ToArray();

        return safe.Length > 0 ? Path.Combine(safe) : string.Empty;
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
