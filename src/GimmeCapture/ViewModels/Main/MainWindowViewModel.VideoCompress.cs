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
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

// "Compress Video" tab: import any video file from disk and re-encode it smaller through the in-process
// LibavClipExporter pipeline. Exposes codec, CRF/target-size, preset, downscale and drop-audio knobs.
public partial class MainWindowViewModel
{
    /// <summary>Set by the view: opens a folder picker for the batch output folder; returns the chosen dir (or null).</summary>
    public Func<Task<string?>>? PickCompressOutputFolderAction { get; set; }

    /// <summary>Set by the view: opens the standalone 進階影片編輯 window for a prepared view model.</summary>
    internal Action<VideoEditViewModel>? OpenEditorAction { get; set; }

    public ReactiveCommand<CompressQueueItem?, Unit> OpenEditorCommand { get; private set; } = null!;

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

    // HandBrake-style video filters (safe 1:1 subset), applied on the 輸出設定 tab and folded into every encode
    // via the settings snapshot. Denoise/Sharpen are strength pickers; Deblock/Grayscale are on/off toggles.
    public sealed class CompressDenoiseOption
    {
        public DenoiseMode Value { get; init; }
        public string Label => LocalizationService.Instance[$"CompressDenoise{Value}"];
    }

    public CompressDenoiseOption[] CompressDenoiseOptions { get; } =
    [
        new CompressDenoiseOption { Value = DenoiseMode.Off },
        new CompressDenoiseOption { Value = DenoiseMode.Light },
        new CompressDenoiseOption { Value = DenoiseMode.Medium },
        new CompressDenoiseOption { Value = DenoiseMode.Strong },
        new CompressDenoiseOption { Value = DenoiseMode.NLMeans }
    ];

    private CompressDenoiseOption? _selectedCompressDenoise;
    public CompressDenoiseOption? SelectedCompressDenoise
    {
        get => _selectedCompressDenoise;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressDenoise, value);
    }

    public sealed class CompressSharpenOption
    {
        public SharpenMode Value { get; init; }
        public string Label => LocalizationService.Instance[$"CompressSharpen{Value}"];
    }

    public CompressSharpenOption[] CompressSharpenOptions { get; } =
    [
        new CompressSharpenOption { Value = SharpenMode.Off },
        new CompressSharpenOption { Value = SharpenMode.Light },
        new CompressSharpenOption { Value = SharpenMode.Medium },
        new CompressSharpenOption { Value = SharpenMode.Strong }
    ];

    private CompressSharpenOption? _selectedCompressSharpen;
    public CompressSharpenOption? SelectedCompressSharpen
    {
        get => _selectedCompressSharpen;
        set => this.RaiseAndSetIfChanged(ref _selectedCompressSharpen, value);
    }

    private bool _compressDeblock;
    public bool CompressDeblock
    {
        get => _compressDeblock;
        set => this.RaiseAndSetIfChanged(ref _compressDeblock, value);
    }

    private bool _compressGrayscale;
    public bool CompressGrayscale
    {
        get => _compressGrayscale;
        set => this.RaiseAndSetIfChanged(ref _compressGrayscale, value);
    }

    // Codec picker. H.264 / H.265 mirror the Record tab; AV1 (SVT-AV1) is compress-only (offline) — the most
    // efficient codec, for the smallest files at the same quality. Reuses the Record tab's option type / labels.
    public RecordingSettingsViewModel.VideoCodecOption[] CompressCodecOptions { get; } =
    [
        new RecordingSettingsViewModel.VideoCodecOption { Value = VideoCodec.H264 },
        new RecordingSettingsViewModel.VideoCodecOption { Value = VideoCodec.H265 },
        new RecordingSettingsViewModel.VideoCodecOption { Value = VideoCodec.Av1 }
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

    // Output container. MP4/MKV/MOV go through LibavClipExporter; GIF/WebM render an edited intermediate mp4
    // then transcode (see EncodeGifWebmAsync), matching the formats Pin mode offers.
    public string[] CompressOutputFormats { get; } = ["MP4", "MKV", "MOV", "GIF", "WebM"];

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

    // Per-item output-settings picker choices: a shared "current settings" sentinel + the saved bundles,
    // used as the ItemsSource for every queue row's dropdown. Each item stores its own SelectedPreset;
    // the sentinel means "use the live 輸出設定", a real bundle overrides those settings for that file.
    internal readonly CompressPreset _currentSettingsChoice = new();
    public ObservableCollection<CompressPreset> BatchPresetChoices { get; } = new();

    private void InitBatchPresetChoices()
    {
        // Sentinel keeps an EMPTY Name (real bundles always have a name) — the row's item template shows the
        // localized "current settings" label for it, so it re-localizes when the language changes.
        BatchPresetChoices.Add(_currentSettingsChoice);
        foreach (CompressPreset p in CompressPresets)
        {
            BatchPresetChoices.Add(p);
        }

        // Mirror preset add/remove incrementally (never Clear the shared list — that would drop every row's
        // selection). A removed bundle sends the rows that used it back to "current settings".
        CompressPresets.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (CompressPreset p in e.NewItems.Cast<CompressPreset>())
                {
                    BatchPresetChoices.Add(p);
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (CompressPreset p in e.OldItems.Cast<CompressPreset>())
                {
                    BatchPresetChoices.Remove(p);
                    foreach (CompressQueueItem item in CompressQueue)
                    {
                        if (ReferenceEquals(item.SelectedPreset, p))
                        {
                            item.SelectedPreset = _currentSettingsChoice;
                        }
                    }
                }
            }
            else
            {
                BatchPresetChoices.Clear();
                BatchPresetChoices.Add(_currentSettingsChoice);
                foreach (CompressPreset p in CompressPresets)
                {
                    BatchPresetChoices.Add(p);
                }
            }
        };
    }

    // The effective encode snapshot for an item: its own bundle if one is picked, else the live settings.
    private CompressSettingsSnapshot BuildSettingsSnapshot(CompressQueueItem? item)
    {
        if (item?.SelectedPreset is { } p && !ReferenceEquals(p, _currentSettingsChoice))
        {
            return SnapshotFromPreset(p);
        }

        return BuildSettingsSnapshot();
    }

    private CompressSettingsSnapshot SnapshotFromPreset(CompressPreset p) => new(
        p.Codec,
        CompressEncodeMath.EncoderScaleCrf(p.Codec, p.Crf),
        p.MaxHeight,
        p.MaxFps,
        CompressPresetOptions.Contains(p.Preset) ? p.Preset : "veryfast",
        p.DropAudio,
        p.AudioBitrateKbps,
        p.AudioChannels,
        p.UseTargetSize,
        p.TargetSizeMB > 0 ? p.TargetSizeMB : 25m,
        p.UseTargetSize && p.Codec == VideoCodec.H264,
        p.Denoise,
        p.Sharpen,
        p.Deblock,
        p.Grayscale);

    // Output container/extension for an item: its bundle's format if picked, else the live format.
    private string EffectiveFormatFor(CompressQueueItem? item)
    {
        string fmt = item?.SelectedPreset is { } p && !ReferenceEquals(p, _currentSettingsChoice)
            ? p.Format
            : SelectedCompressFormat;
        return CompressOutputFormats.Contains(fmt) ? fmt : SelectedCompressFormat;
    }

    // A built-in "quick recipe": applies a bundle of the global encode knobs in one pick. Distinct from the
    // user-savable CompressPreset (those persist); these are fixed. IsCustom is a no-op "leave as-is" entry.
    public sealed class CompressQuickProfile
    {
        public string Key { get; init; } = string.Empty;
        public bool IsCustom { get; init; }
        public VideoCodec Codec { get; init; }
        public int Crf { get; init; }
        public int MaxHeight { get; init; }             // 0 = keep source resolution
        public string Preset { get; init; } = "medium";
        public bool UseTargetSize { get; init; }
        public decimal TargetSizeMB { get; init; }
        public string Format { get; init; } = "MP4";
        public bool DropAudio { get; init; }
        public string Name => LocalizationService.Instance[Key];
    }

    // Built-in quick recipes. Static so tests can assert the table without constructing the view model.
    internal static CompressQuickProfile[] BuildCompressQuickProfiles() =>
    [
        new CompressQuickProfile { Key = "CompressQuickCustom", IsCustom = true },
        new CompressQuickProfile { Key = "CompressQuickDiscord", Codec = VideoCodec.H264, Crf = 23, MaxHeight = 720, Preset = "fast", UseTargetSize = true, TargetSizeMB = 25m, Format = "MP4" },
        new CompressQuickProfile { Key = "CompressQuickWeb1080", Codec = VideoCodec.H264, Crf = 23, MaxHeight = 1080, Preset = "medium", Format = "MP4" },
        new CompressQuickProfile { Key = "CompressQuickSmallest", Codec = VideoCodec.H265, Crf = 30, MaxHeight = 480, Preset = "medium", Format = "MP4" },
        new CompressQuickProfile { Key = "CompressQuickHighQuality", Codec = VideoCodec.H265, Crf = 18, MaxHeight = 0, Preset = "slow", Format = "MP4" },
        // Headline "smallest at the same quality" recipe: AV1 (10-bit, ~30% smaller than H.265) at a slow
        // preset, full resolution. The CRF here is the UI (x265) scale; the +8 AV1 offset is applied on encode.
        new CompressQuickProfile { Key = "CompressQuickSmallestAv1", Codec = VideoCodec.Av1, Crf = 24, MaxHeight = 1080, Preset = "slow", Format = "MP4" },
    ];

    public CompressQuickProfile[] CompressQuickProfiles { get; } = BuildCompressQuickProfiles();

    private CompressQuickProfile? _selectedQuickProfile;
    // Picking a non-Custom recipe applies its knobs to the controls below; Custom is a no-op placeholder.
    public CompressQuickProfile? SelectedQuickProfile
    {
        get => _selectedQuickProfile;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedQuickProfile, value);
            if (value is { IsCustom: false })
            {
                ApplyQuickProfile(value);
            }
        }
    }

    // Applies a quick recipe through the public setters, so the estimate recompute + session-save subscriptions
    // fire automatically (same path as loading a saved preset). Values are guarded against the fixed option lists.
    private void ApplyQuickProfile(CompressQuickProfile p)
    {
        SelectedCompressCodecOption =
            Array.Find(CompressCodecOptions, o => o.Value == p.Codec) ?? SelectedCompressCodecOption;
        if (p.Crf > 0) CompressCrf = Math.Clamp(p.Crf, 14, 40);
        SelectedCompressResolution =
            Array.Find(CompressResolutionOptions, o => o.MaxHeight == p.MaxHeight) ?? SelectedCompressResolution;
        if (CompressPresetOptions.Contains(p.Preset)) SelectedCompressPreset = p.Preset;
        CompressUseTargetSize = p.UseTargetSize;
        if (p.UseTargetSize) CompressTargetSizeMB = p.TargetSizeMB;
        SelectedCompressFormat = CompressOutputFormats.Contains(p.Format) ? p.Format : SelectedCompressFormat;
        CompressDropAudio = p.DropAudio;
    }

    private void InitializeVideoCompress()
    {
        _selectedCompressResolution = CompressResolutionOptions[0]; // Original
        _selectedCompressFps = CompressFpsOptions[0]; // source rate
        _selectedCompressAudioBitrate = CompressAudioBitrateOptions[0]; // Auto
        _selectedCompressAudioChannels = CompressAudioChannelsOptions[0]; // Stereo
        _selectedCompressDenoise = CompressDenoiseOptions[0]; // Off
        _selectedCompressSharpen = CompressSharpenOptions[0]; // Off

        // Default to the HandBrake "shrink ~80%, near-lossless" recipe: H.265 (with CRF 22 / medium above).
        _selectedCompressCodecOption = Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H265)
            ?? Array.Find(CompressCodecOptions, o => o.Value == VideoCodec.H264);

        _selectedQuickProfile = CompressQuickProfiles[0]; // seed to Custom via the field (no auto-apply)

        CompressStatusText = LocalizationService.Instance["CompressStatusReady"];

        var notBusy = this.WhenAnyValue(x => x.IsBusy, busy => !busy);

        // Output folder: an optional batch destination. Pickable when nothing is running; clear to revert
        // to auto-save-next-to-source.
        PickCompressOutputFolderCommand = ReactiveCommand.CreateFromTask(PickCompressOutputFolderAsync, notBusy);
        ClearCompressOutputFolderCommand = ReactiveCommand.Create(
            () => { CompressOutputFolder = string.Empty; },
            this.WhenAnyValue(x => x.CompressOutputFolder, x => x.IsBusy,
                (folder, busy) => !string.IsNullOrEmpty(folder) && !busy));

        // Advanced video editing: per-item (parameter) with the tab's selection as fallback; idle queue only.
        OpenEditorCommand = ReactiveCommand.Create<CompressQueueItem?>(
            OpenEditor,
            this.WhenAnyValue(x => x.IsBusy, (bool busy) => !busy));
        OpenEditorCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.OpenEditor", ex));

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
        InitBatchPresetChoices();

        // EstimatedText is a cached localized string; re-localize the labels when the language changes.
        LocalizationService.Instance.WhenAnyValue(x => x.CurrentLanguage)
            .Skip(1)
            .Subscribe(_ => RecomputeQueueEstimates());

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
        // Video filters drive the estimate + persist too — kept as their own subscription to stay under
        // WhenAnyValue's argument cap (the two blocks above are already near it).
        this.WhenAnyValue(
                x => x.SelectedCompressDenoise, x => x.SelectedCompressSharpen,
                x => x.CompressDeblock, x => x.CompressGrayscale,
                (a, b, c, d) => Unit.Default)
            .Subscribe(_ => RecomputeQueueEstimates());
        this.WhenAnyValue(
                x => x.SelectedCompressDenoise, x => x.SelectedCompressSharpen,
                x => x.CompressDeblock, x => x.CompressGrayscale,
                (a, b, c, d) => Unit.Default)
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
        AudioChannels = SelectedCompressAudioChannels?.Channels ?? 2,
        Denoise = SelectedCompressDenoise?.Value ?? DenoiseMode.Off,
        Sharpen = SelectedCompressSharpen?.Value ?? SharpenMode.Off,
        Deblock = CompressDeblock,
        Grayscale = CompressGrayscale
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
        SelectedCompressDenoise = Array.Find(CompressDenoiseOptions, o => o.Value == p.Denoise) ?? CompressDenoiseOptions[0];
        SelectedCompressSharpen = Array.Find(CompressSharpenOptions, o => o.Value == p.Sharpen) ?? CompressSharpenOptions[0];
        CompressDeblock = p.Deblock;
        CompressGrayscale = p.Grayscale;
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
        double dur = item.EffectiveOutputDuration; // speed-adjusted output span (whole file when untrimmed)
        if (dur <= 0 || item.ProbedWidth <= 0 || item.ProbedHeight <= 0)
        {
            return string.Empty; // not probed yet (or unreadable)
        }

        string prefix = LocalizationService.Instance["CompressEstimateLabel"];
        CompressSettingsSnapshot s = BuildSettingsSnapshot(item); // per-item bundle, or the live settings

        // GIF/WebM aren't bitrate/CRF-modelled by the bpp estimator (palette / VP9), so don't show a misleading
        // number — the size depends heavily on content, resolution, and frame rate.
        string fmt = EffectiveFormatFor(item);
        if (fmt.Equals("GIF", StringComparison.OrdinalIgnoreCase) || fmt.Equals("WebM", StringComparison.OrdinalIgnoreCase))
        {
            return $"{prefix}: {LocalizationService.Instance["CompressEstimateVaries"]}";
        }

        if (s.UseTargetSize)
        {
            // Target-size mode encodes to (approximately) the requested size by design.
            return $"{prefix}: ≈ {FileSizeFormatter.Format((long)((double)s.TargetSizeMB * 1024 * 1024))}";
        }

        int audioKbps = s.DropAudio ? 0 : (s.AudioBitrateKbps > 0 ? s.AudioBitrateKbps : 128);
        long est = CompressEncodeMath.EstimateOutputSizeBytes(
            item.ProbedWidth, item.ProbedHeight, item.ProbedFps, dur, s.MaxHeight, s.MaxFps, s.Codec, s.Crf, audioKbps);
        return $"{prefix}: ≈ {FileSizeFormatter.Format(est)}";
    }

    // CompressSettingsSnapshot (the immutable encode-knobs snapshot) now lives in
    // Services/Core/Media/CompressSettingsSnapshot.cs so the compress pipeline can consume it directly.
    // The AV1 CRF offset + EncoderScaleCrf mapping now live in Services/Core/Media/CompressEncodeMath.cs.

    private CompressSettingsSnapshot BuildSettingsSnapshot()
    {
        VideoCodec codec = SelectedCompressCodecOption?.Value ?? VideoCodec.H264;
        return new CompressSettingsSnapshot(
            codec,
            CompressEncodeMath.EncoderScaleCrf(codec, CompressCrf),
            SelectedCompressResolution?.MaxHeight ?? 0,
            SelectedCompressFps?.Fps ?? 0,
            SelectedCompressPreset,
            CompressDropAudio,
            SelectedCompressAudioBitrate?.Kbps ?? 0,
            SelectedCompressAudioChannels?.Channels ?? 0,
            CompressUseTargetSize,
            CompressTargetSizeMB ?? 25m,
            // True two-pass replaces the corrective pass for H.264 target-size; H.265 keeps single-pass + correct.
            CompressUseTargetSize && codec == VideoCodec.H264,
            SelectedCompressDenoise?.Value ?? DenoiseMode.Off,
            SelectedCompressSharpen?.Value ?? SharpenMode.Off,
            CompressDeblock,
            CompressGrayscale);
    }

    // Toast shown when the user pauses a non-CRF file: only CRF mode can resume mid-encode after a restart.
    private void ShowPauseNoResumeWarning() =>
        ShowToastAction?.Invoke(LocalizationService.Instance["CompressPauseNoResume"], ToastSeverity.Info);

    // Builds a CompareViewModel for the given file with the current encode settings, anchored at the editor's
    // playhead and using the editor's live rotation. Returns null if there is no item. The editor hosts the
    // result inline (no separate window).
    private CompareViewModel? BuildCompareVm(CompressQueueItem? target, double anchorSeconds, int rotationDegrees)
    {
        CompressQueueItem? item = target ?? SelectedQueueItem;
        if (item == null)
        {
            return null;
        }

        CompressSettingsSnapshot snap = BuildSettingsSnapshot();
        int targetKbps = snap.UseTargetSize && item.ProbedDuration > 0
            ? CompressEncodeMath.ComputeTargetVideoBitrateKbps((double)snap.TargetSizeMB, item.ProbedDuration)
            : 0;

        // Rotation is applied at display time to BOTH compare frames, not baked into the sample.
        var options = snap.ToExportOptions(rotationDegrees: 0, targetVideoBitrateKbps: targetKbps);

        return new CompareViewModel(
            item.Path, item.ProbedDuration, item.ProbedFps, item.ProbedWidth, item.ProbedHeight,
            options, rotationDegrees, anchorSeconds);
    }

    // Opens the 進階影片編輯 window for the selected file; on Apply writes the whole edit (kept runs with speed,
    // crop, rotation) back to the item (the property sets trigger the existing persist + re-estimate + summary).
    private void OpenEditor(CompressQueueItem? target)
    {
        CompressQueueItem? item = target ?? SelectedQueueItem;
        if (item == null || OpenEditorAction == null || item.ProbedDuration <= 0)
        {
            return;
        }

        SelectedQueueItem = item; // keep the tab's selection in sync with the row that was acted on

        var initial = new VideoEditResult(
            item.EffectiveKeptRuns().Select(r => new VideoEditSegment(r.Start, r.End, r.Speed)).ToList(),
            item.Crop,
            item.Rotation,
            item.Annotations ?? Array.Empty<Annotation>(),
            item.AnnotationSurfaceWidth,
            item.AnnotationSurfaceHeight,
            item.RedactionTracks ?? Array.Empty<RedactionTrack>());

        // The onApply lambda needs the editor VM (for the output filename typed there), but it is
        // passed INTO the VM's constructor — capture via a local set right after construction.
        VideoEditViewModel? editorVm = null;
        var vm = new VideoEditViewModel(
            item.Path, item.ProbedDuration, item.ProbedFps, item.ProbedWidth, item.ProbedHeight,
            initial,
            result =>
            {
                item.TrimEnabled = true;
                item.KeptSegments = result.KeptRuns;
                item.Crop = result.Crop;
                item.Rotation = result.RotationDegrees;
                item.Annotations = result.Annotations.Count > 0 ? result.Annotations : null;
                item.AnnotationSurfaceWidth = result.AnnotationSurfaceWidth;
                item.AnnotationSurfaceHeight = result.AnnotationSurfaceHeight;
                item.RedactionTracks = result.RedactionTracks.Count > 0 ? result.RedactionTracks : null;
                if (editorVm != null)
                {
                    item.OutputName = editorVm.OutputFileName;
                }
                RefreshSelectedPreview(item);
            },
            CloneBitmapForEditor(item.Thumbnail)); // placeholder preview while the first frame decodes. A CLONE,
        // not the shared instance: the queue item owns its Thumbnail and may be disposed (e.g. 清空) while this
        // non-modal editor is still rendering the placeholder — a shared bitmap would crash on the next render.
        editorVm = vm;

        // Output filename + quality compare moved from the 編輯 tab into the editor window.
        vm.OutputFileName = item.OutputName;
        vm.BuildCompareViewModel = (anchor, rotation) => BuildCompareVm(item, anchor, rotation);

        OpenEditorAction(vm);
    }

    // Independent copy of a queue-item thumbnail for the editor's placeholder preview, so disposing the item's
    // own Thumbnail (on 清空) can't free a bitmap the still-open editor is rendering. Small (~360px) → cheap.
    private static Avalonia.Media.Imaging.Bitmap? CloneBitmapForEditor(Avalonia.Media.Imaging.Bitmap? src)
    {
        if (src == null)
        {
            return null;
        }

        try
        {
            using var ms = new System.IO.MemoryStream();
            src.Save(ms);
            ms.Position = 0;
            return new Avalonia.Media.Imaging.Bitmap(ms);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CloneThumbnail", ex);
            return null;
        }
    }

}
