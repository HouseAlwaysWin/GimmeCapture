using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using Avalonia.Media.Imaging;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.Models;

// Batch-queue row model for the Compress tab. Moved out of MainWindowViewModel (was a nested type) — it is a
// self-contained ReactiveObject that talks to the VM only through the StartRequested/EstimateRequested/
// PauseWarningRequested callbacks, so it belongs with the other Models.
public enum CompressQueueStatus
{
    Queued,    // added, not started
    Waiting,   // started, waiting for a concurrency slot
    Running,
    Done,
    Failed,
    Cancelled
}

public sealed class CompressQueueItem : ReactiveObject, IDisposable
{
    public CompressQueueItem(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        (FileSizeText, DateText) = ReadFileMeta(path);
        _outputName = System.IO.Path.GetFileNameWithoutExtension(path); // editable; may include \subfolder\
        StartCommand = ReactiveCommand.Create(() => StartRequested?.Invoke(this), this.WhenAnyValue(x => x.CanStart));
        PauseCommand = ReactiveCommand.Create(Pause, this.WhenAnyValue(x => x.ShowPause));
        ResumeCommand = ReactiveCommand.Create(Resume, this.WhenAnyValue(x => x.ShowResume));
        CancelCommand = ReactiveCommand.Create(Cancel, this.WhenAnyValue(x => x.CanCancel));
        EstimateCommand = ReactiveCommand.Create(
            () => EstimateRequested?.Invoke(this),
            this.WhenAnyValue(x => x.IsEstimating, x => x.Status, (est, st) => !est && st != CompressQueueStatus.Running));
        RevealCommand = ReactiveCommand.Create(Reveal, this.WhenAnyValue(x => x.HasOutput));
        UpdateStatusText();
    }

    public string Path { get; }
    public string FileName { get; }

    /// <summary>Formatted source file size (e.g. "12.3 MB"), shown in the picker row.</summary>
    public string FileSizeText { get; }

    /// <summary>Formatted source file last-write date, shown in the picker row.</summary>
    public string DateText { get; }

    private Bitmap? _thumbnail;
    /// <summary>First-frame thumbnail for the picker row; decoded lazily after the file is queued.</summary>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        internal set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
    }

    /// <summary>Releases the decoded thumbnail bitmap. Call when the item leaves the queue (e.g. 清空).</summary>
    public void Dispose()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
    }

    private static (string Size, string Date) ReadFileMeta(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            if (fi.Exists)
            {
                return (FileSizeFormatter.Format(fi.Length), fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
            }
        }
        catch
        {
            // best effort — a missing/locked file just shows no size/date
        }

        return (string.Empty, string.Empty);
    }

    // User-editable output name. May carry a relative subfolder path (e.g. "\sub\clip"); the date stamp
    // and extension are added when the path is composed. Blank falls back to the source name.
    private string _outputName = string.Empty;
    public string OutputName
    {
        get => _outputName;
        set => this.RaiseAndSetIfChanged(ref _outputName, value);
    }

    // Per-item output-settings bundle. The "current settings" sentinel (set as the default) means "use
    // the live 輸出設定 tab"; a real saved bundle overrides those settings for this file only.
    private CompressPreset? _selectedPreset;
    public CompressPreset? SelectedPreset
    {
        get => _selectedPreset;
        set => this.RaiseAndSetIfChanged(ref _selectedPreset, value);
    }

    // Per-item output category (a subfolder of the active working area) chosen from the row dropdown. null /
    // the "default" sentinel (CompressCategoryChoices[0], an empty string) means "use the global 輸出設定
    // folder, else next to the source"; otherwise this is the full path of the chosen category subfolder.
    private string? _selectedOutputDir = string.Empty;
    public string? SelectedOutputDir
    {
        get => _selectedOutputDir;
        set => this.RaiseAndSetIfChanged(ref _selectedOutputDir, value);
    }

    // The full path of the file this item produced, set on success. Drives the per-row "open output
    // folder" button (it needs a concrete existing file for explorer /select).
    private string? _outputPath;
    public string? OutputPath
    {
        get => _outputPath;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _outputPath, value);
            this.RaisePropertyChanged(nameof(HasOutput));
        }
    }

    /// <summary>True once this item has finished and recorded a produced output path (enables 開啟資料夾).</summary>
    public bool HasOutput => Status == CompressQueueStatus.Done && !string.IsNullOrEmpty(OutputPath);

    // Set by the view model so the per-item Start / Estimate buttons can call back into it.
    internal Action<CompressQueueItem>? StartRequested;
    internal Action<CompressQueueItem>? EstimateRequested;

    // True while a sample-encode accurate estimate is running for this item.
    private bool _isEstimating;
    public bool IsEstimating
    {
        get => _isEstimating;
        internal set => this.RaiseAndSetIfChanged(ref _isEstimating, value);
    }

    // Per-item cancellation (created at start, linked to the batch token) + pause gate (created at encode).
    internal CancellationTokenSource? Cts;
    internal ManualResetEventSlim? Gate;

    private CompressQueueStatus _status = CompressQueueStatus.Queued;
    public CompressQueueStatus Status
    {
        get => _status;
        set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            UpdateStatusText();
            this.RaisePropertyChanged(nameof(ShowPause));
            this.RaisePropertyChanged(nameof(ShowResume));
            this.RaisePropertyChanged(nameof(CanCancel));
            this.RaisePropertyChanged(nameof(CanStart));
            this.RaisePropertyChanged(nameof(IsActive));
            this.RaisePropertyChanged(nameof(ShowProgress));
            this.RaisePropertyChanged(nameof(ShowStart));
            this.RaisePropertyChanged(nameof(ShowColdResume));
            this.RaisePropertyChanged(nameof(ShowResumeHint));
            this.RaisePropertyChanged(nameof(HasOutput));
        }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    // Probed source metadata (filled asynchronously after the item is added) for the per-row size estimate.
    public int ProbedWidth { get; internal set; }
    public int ProbedHeight { get; internal set; }
    public int ProbedFps { get; internal set; }
    public double ProbedDuration { get; internal set; }

    // "≈ X MB" estimate for this file under the current output settings (recomputed by the view model).
    private string _estimatedText = string.Empty;
    public string EstimatedText
    {
        get => _estimatedText;
        internal set => this.RaiseAndSetIfChanged(ref _estimatedText, value);
    }

    // Per-file output rotation, baked into the pixels at encode time (0/90/180/270 clockwise).
    private int _rotation;
    public int Rotation
    {
        get => _rotation;
        set
        {
            this.RaiseAndSetIfChanged(ref _rotation, value);
            RaiseEditSummary();
        }
    }

    // Optional per-file crop (source pixels), applied before rotation at encode. null = no crop.
    private VideoEditCrop? _crop;
    public VideoEditCrop? Crop
    {
        get => _crop;
        set
        {
            this.RaiseAndSetIfChanged(ref _crop, value);
            RaiseEditSummary();
        }
    }

    // Optional burn-in layers from the 進階影片編輯 editor: annotations (drawn in the editor's surface
    // space — the cropped+rotated preview-frame pixel size recorded below) and redaction tracks
    // (normalized [0,1]). Burned into the frames post-transform at encode.
    private IReadOnlyList<Annotation>? _annotations;
    public IReadOnlyList<Annotation>? Annotations
    {
        get => _annotations;
        set
        {
            this.RaiseAndSetIfChanged(ref _annotations, value);
            RaiseEditSummary();
        }
    }

    /// <summary>The surface (reference) size the annotations were drawn against.</summary>
    public double AnnotationSurfaceWidth { get; set; }
    public double AnnotationSurfaceHeight { get; set; }

    private IReadOnlyList<RedactionTrack>? _redactionTracks;
    public IReadOnlyList<RedactionTrack>? RedactionTracks
    {
        get => _redactionTracks;
        set
        {
            this.RaiseAndSetIfChanged(ref _redactionTracks, value);
            RaiseEditSummary();
        }
    }

    /// <summary>True when annotations/redaction must be burned per frame (forces the whole-file path).</summary>
    public bool HasBurnInEdits =>
        (_annotations?.Count ?? 0) > 0
        || (_redactionTracks?.Any(t => t.Keyframes.Count > 0) ?? false);

    // Optional per-file edit: the kept runs (source [start,end] pieces, each optionally time-scaled)
    // concatenated at encode. Empty/off = whole clip. Edited in the 進階影片編輯 window; persisted per file.
    private bool _trimEnabled;
    public bool TrimEnabled
    {
        get => _trimEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _trimEnabled, value);
            RaiseEditSummary();
        }
    }

    private IReadOnlyList<VideoEditSegment>? _keptSegments;
    /// <summary>The kept runs (source ranges, each with Speed) to concatenate; null/empty = whole clip.</summary>
    public IReadOnlyList<VideoEditSegment>? KeptSegments
    {
        get => _keptSegments;
        set
        {
            this.RaiseAndSetIfChanged(ref _keptSegments, value);
            RaiseEditSummary();
        }
    }

    /// <summary>The kept runs (Start, End, Speed) clamped to the probed duration (whole clip when off/empty).</summary>
    public IReadOnlyList<(double Start, double End, double Speed)> EffectiveKeptRuns()
    {
        double full = ProbedDuration > 0 ? ProbedDuration : 0;
        if (!TrimEnabled || _keptSegments is not { Count: > 0 } || full <= 0)
        {
            return full > 0
                ? new[] { (0.0, full, 1.0) }
                : Array.Empty<(double, double, double)>();
        }

        var runs = _keptSegments
            .Select(s => (Start: Math.Clamp(s.SourceStart, 0, full), End: Math.Clamp(s.SourceEnd, 0, full),
                Speed: s.Speed > 0 ? s.Speed : 1.0))
            .Where(r => r.End > r.Start + 0.001)
            .OrderBy(r => r.Start)
            .ToList();
        return runs.Count > 0 ? runs : new List<(double Start, double End, double Speed)> { (0.0, full, 1.0) };
    }

    /// <summary>Encoded source span (seconds) = sum of the kept runs; used where source time matters.</summary>
    public double EffectiveDuration => EffectiveKeptRuns().Sum(r => Math.Max(0, r.End - r.Start));

    /// <summary>Output span (seconds) = Σ each kept run's source length ÷ its speed. Drives bitrate/estimate.</summary>
    public double EffectiveOutputDuration =>
        EffectiveKeptRuns().Sum(r => Math.Max(0, r.End - r.Start) / (r.Speed > 0 ? r.Speed : 1.0));

    private bool RunsAreTrimmed(IReadOnlyList<(double Start, double End, double Speed)> runs) =>
        runs.Count > 1
        || (runs.Count == 1 && (runs[0].Start > 0.05 || (ProbedDuration > 0 && runs[0].End < ProbedDuration - 0.05)));

    /// <summary>True when the clip carries any edit (trim, speed, crop, rotation, or burn-in layers).</summary>
    public bool HasEdits
    {
        get
        {
            IReadOnlyList<(double Start, double End, double Speed)> runs = EffectiveKeptRuns();
            bool retimed = runs.Any(r => Math.Abs(r.Speed - 1.0) > 0.001);
            return RunsAreTrimmed(runs) || retimed || Crop != null || Rotation != 0 || HasBurnInEdits;
        }
    }

    /// <summary>Human summary of the applied edits ("3 段 (0:12) · 變速 · 裁切 · 旋轉 90°"), shown on the 編輯 tab.</summary>
    public string EditSummaryText
    {
        get
        {
            LocalizationService loc = LocalizationService.Instance;
            IReadOnlyList<(double Start, double End, double Speed)> runs = EffectiveKeptRuns();
            if (!HasEdits)
            {
                return loc["CompressEditNone"];
            }

            static string F(double sec) => TimeSpan.FromSeconds(Math.Max(0, sec)).ToString(@"m\:ss");
            var parts = new List<string>();
            if (RunsAreTrimmed(runs))
            {
                double outLen = runs.Sum(r => Math.Max(0, r.End - r.Start) / (r.Speed > 0 ? r.Speed : 1.0));
                parts.Add($"{runs.Count} {loc["CompressTrimSegments"]} ({F(outLen)})");
            }
            if (runs.Any(r => Math.Abs(r.Speed - 1.0) > 0.001))
            {
                parts.Add(loc["CompressEditSpeed"]);
            }
            if (Crop != null)
            {
                parts.Add(loc["CompressEditCrop"]);
            }
            if (Rotation != 0)
            {
                parts.Add($"{loc["CompressEditRotate"]} {Rotation}°");
            }
            if ((_annotations?.Count ?? 0) > 0)
            {
                parts.Add(loc["CompressEditAnnotated"]);
            }
            if (_redactionTracks?.Any(t => t.Keyframes.Count > 0) ?? false)
            {
                parts.Add(loc["CompressEditRedacted"]);
            }
            return string.Join(" · ", parts);
        }
    }

    // Re-raise the edit summary + edited flag (probed duration landed, or an edit was applied).
    internal void RaiseEditSummary()
    {
        this.RaisePropertyChanged(nameof(EditSummaryText));
        this.RaisePropertyChanged(nameof(HasEdits));
    }

    // True for a file restored from a previous run where it was paused: shown as paused at its last
    // progress until the user resumes (which re-encodes the whole file from the start).
    private bool _wasPaused;
    public bool WasPaused
    {
        get => _wasPaused;
        set
        {
            this.RaiseAndSetIfChanged(ref _wasPaused, value);
            UpdateStatusText();
            this.RaisePropertyChanged(nameof(ShowProgress));
            this.RaisePropertyChanged(nameof(ShowStart));
            this.RaisePropertyChanged(nameof(ShowColdResume));
        }
    }

    // Whether this run can resume after a restart (CRF mode with a known duration → segmented encode).
    private bool _supportsResume;
    public bool SupportsResume
    {
        get => _supportsResume;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _supportsResume, value);
            this.RaisePropertyChanged(nameof(ShowResumeHint));
        }
    }

    // Last safe resume checkpoint (0-1): pausing now re-does only the part past this point. CRF only.
    private double _resumePoint;
    public double ResumePoint
    {
        get => _resumePoint;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _resumePoint, value);
            this.RaisePropertyChanged(nameof(ResumePointText));
        }
    }

    public bool ShowResumeHint => SupportsResume && Status == CompressQueueStatus.Running;
    public string ResumePointText =>
        $"{LocalizationService.Instance["CompressResumePoint"]} {_resumePoint:P0}";

    // Invoked when the user pauses an item that can't be resumed after a restart (non-CRF mode).
    public Action? PauseWarningRequested { get; set; }

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

    // Start when idle/terminal; cancel when queued/waiting/running; pause/resume only while running.
    public bool CanStart => Status is CompressQueueStatus.Queued or CompressQueueStatus.Done
        or CompressQueueStatus.Failed or CompressQueueStatus.Cancelled;
    public bool CanCancel => Status is CompressQueueStatus.Queued or CompressQueueStatus.Waiting
        or CompressQueueStatus.Running;
    public bool ShowPause => Status == CompressQueueStatus.Running && !IsPaused;
    public bool ShowResume => Status == CompressQueueStatus.Running && IsPaused;
    public bool IsActive => Status == CompressQueueStatus.Running;
    // Progress bar shows while encoding or when restored as cold-paused.
    public bool ShowProgress => Status == CompressQueueStatus.Running || WasPaused;
    // A cold-paused file shows a "繼續" button (re-encodes from 0) instead of the normal "啟動".
    public bool ShowStart => CanStart && !WasPaused;
    public bool ShowColdResume => CanStart && WasPaused;

    public ReactiveCommand<Unit, Unit> StartCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }
    public ReactiveCommand<Unit, Unit> ResumeCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> EstimateCommand { get; }
    public ReactiveCommand<Unit, Unit> RevealCommand { get; }

    // Open the produced file's folder in Explorer (selected). Gated on HasOutput so it only runs once the
    // file exists; RevealInFileExplorer additionally guards File.Exists internally.
    private void Reveal()
    {
        if (!string.IsNullOrEmpty(OutputPath))
        {
            FileLocationService.RevealInFileExplorer(OutputPath);
        }
    }

    // Prepares the item just before a worker is launched (caller has ensured a batch context exists).
    internal void PrepareForStart(CancellationToken batchToken)
    {
        Cts?.Dispose();
        Cts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
        IsPaused = false;
        WasPaused = false; // a fresh start clears the restored-paused marker
        Progress = 0;
        Status = CompressQueueStatus.Waiting;
    }

    private void Pause()
    {
        if (!SupportsResume)
        {
            PauseWarningRequested?.Invoke(); // non-CRF: closing now will restart this file from the start
        }
        Gate?.Reset();
        IsPaused = true;
    }

    private void Resume()
    {
        Gate?.Set();
        IsPaused = false;
    }

    private void Cancel()
    {
        Gate?.Set(); // release a paused encode so it can observe cancellation
        IsPaused = false;
        if (Cts != null)
        {
            Cts.Cancel(); // waiting or running
        }
        else if (Status == CompressQueueStatus.Queued)
        {
            Status = CompressQueueStatus.Cancelled; // never dispatched
        }
    }

    private void UpdateStatusText()
    {
        StatusText = LocalizationService.Instance[WasPaused && Status == CompressQueueStatus.Queued
            ? "CompressQueuePaused"
            : Status switch
            {
                CompressQueueStatus.Waiting => "CompressQueueWaiting",
                CompressQueueStatus.Running => "CompressQueueRunning",
                CompressQueueStatus.Done => "CompressQueueDone",
                CompressQueueStatus.Failed => "CompressQueueFailed",
                CompressQueueStatus.Cancelled => "CompressQueueCancelled",
                _ => "CompressQueueQueued"
            }];
    }
}
