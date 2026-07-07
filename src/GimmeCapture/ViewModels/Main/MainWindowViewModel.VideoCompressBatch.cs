using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Floating;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

// Batch queue for the Compress tab. Each file can be started / paused / cancelled individually; a shared
// concurrency cap (CompressParallelCount) bounds how many encode at once, whether started one-by-one or via
// "Compress all". The per-file encode core (EncodeOneFileAsync) is shared with the single-file path.
//
// Threading: every worker is launched from the UI thread and never uses ConfigureAwait(false), so all item
// state mutations + the shared _inFlight/_batchCts/_batchSemaphore bookkeeping run on the UI thread (no locks
// needed). The CPU-heavy encode itself runs on a Task.Run thread inside EncodeOneFileAsync, so parallelism is
// real while the queue plumbing stays single-threaded.
public partial class MainWindowViewModel
{
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
                    return (FormatFileSize(fi.Length), fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
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

    private static readonly string[] CompressVideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv"];

    public ObservableCollection<CompressQueueItem> CompressQueue { get; } = new();

    // Persisted batch "working directories" (source folders). Their videos auto-load into the queue on startup.
    public ObservableCollection<string> CompressWorkingDirectories { get; } = new();

    // ItemsSource for each row's output-category dropdown: an empty-string sentinel ("default": global folder,
    // else next to source) followed by the immediate subfolders (categories) of the ACTIVE working area. Rebuilt
    // whenever the active working area changes and appended to when a category is created. The empty sentinel
    // re-localizes via the row's item template; category entries display their leaf name (FolderLeafNameConverter).
    public ObservableCollection<string> CompressCategoryChoices { get; } = new();

    // New-category name typed in the working-area section; "新增分類" creates a subfolder of the active area.
    private string _newCategoryName = string.Empty;
    public string NewCategoryName
    {
        get => _newCategoryName;
        set => this.RaiseAndSetIfChanged(ref _newCategoryName, value);
    }

    public Func<Task<IReadOnlyList<string>>>? PickCompressFilesAction { get; set; }
    public Func<Task<string?>>? PickCompressFolderAction { get; set; }

    // True while any queue item is waiting/running. IsBusy gates the settings controls during a run.
    private bool _isBatchRunning;
    public bool IsBatchRunning
    {
        get => _isBatchRunning;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isBatchRunning, value);
            this.RaisePropertyChanged(nameof(IsBusy));
        }
    }

    public bool IsBusy => IsBatchRunning;

    // Max items encoding at once. Each encode is already multi-threaded, so a small cap avoids thrashing.
    public int[] CompressParallelOptions { get; } = [1, 2, 3, 4];

    private int _compressParallelCount = 2;
    public int CompressParallelCount
    {
        get => _compressParallelCount;
        set => this.RaiseAndSetIfChanged(ref _compressParallelCount, Math.Clamp(value, 1, 8));
    }

    private int _compressQueueCount;
    public int CompressQueueCount
    {
        get => _compressQueueCount;
        private set => this.RaiseAndSetIfChanged(ref _compressQueueCount, value);
    }

    // Shared batch context, alive only while at least one worker is in flight (created lazily on first start).
    private CancellationTokenSource? _batchCts;
    private SemaphoreSlim? _batchSemaphore;
    private int _inFlight;

    // The queue item shown in the "Edit" accordion (first-frame preview + rotation).
    private CompressQueueItem? _selectedQueueItem;
    public CompressQueueItem? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set => this.RaiseAndSetIfChanged(ref _selectedQueueItem, value);
    }

    // The selected file's first frame, rotated to the chosen angle, for the preview Image.
    private Bitmap? _previewBitmap;
    public Bitmap? PreviewBitmap
    {
        get => _previewBitmap;
        private set => this.RaiseAndSetIfChanged(ref _previewBitmap, value);
    }

    private Bitmap? _rawPreview;            // the un-rotated decoded first frame
    private CancellationTokenSource? _previewCts;

    public ReactiveCommand<Unit, Unit> AddCompressFilesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearCompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelAllCompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> EstimateAllCompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddCompressWorkingDirCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> SwitchCompressWorkingDirCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> RemoveCompressWorkingDirCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddCompressCategoryCommand { get; private set; } = null!;

    private string? _activeWorkingDir;
    // The working directory whose videos are currently loaded in the queue (auto-loaded again next launch).
    public string? ActiveWorkingDir
    {
        get => _activeWorkingDir;
        private set => this.RaiseAndSetIfChanged(ref _activeWorkingDir, value);
    }

    // Per-file edit/progress state (rotation, output name, done), persisted across restarts; keyed by source path.
    private Dictionary<string, CompressItemState> _itemStates = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeCompressBatch()
    {
        // Add anytime (even mid-batch). Compress-all whenever there are startable items. Clear only when idle.
        var canStartAll = this.WhenAnyValue(x => x.CompressQueueCount, count => count > 0);
        var canClear = this.WhenAnyValue(
            x => x.CompressQueueCount, x => x.IsBusy, (count, busy) => count > 0 && !busy);

        AddCompressFilesCommand = ReactiveCommand.CreateFromTask(AddCompressFilesAsync);
        ClearCompressQueueCommand = ReactiveCommand.Create(ClearCompressQueue, canClear);
        CompressQueueCommand = ReactiveCommand.Create(StartAllCompress, canStartAll);
        CancelAllCompressQueueCommand = ReactiveCommand.Create(
            CancelAllCompressQueue, this.WhenAnyValue(x => x.IsBatchRunning));
        // Estimate-all: sample-encode every file; not while a real batch is running (avoids CPU contention).
        EstimateAllCompressQueueCommand = ReactiveCommand.CreateFromTask(
            EstimateAllAsync,
            this.WhenAnyValue(x => x.CompressQueueCount, x => x.IsBatchRunning, (count, busy) => count > 0 && !busy));

        // Load the first-frame preview when the selected file changes.
        this.WhenAnyValue(x => x.SelectedQueueItem).Subscribe(item => _ = LoadPreviewAsync(item));

        AddCompressFilesCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueAddFiles", ex));
        ClearCompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueClear", ex));
        CompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueRun", ex));
        CancelAllCompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueCancelAll", ex));
        EstimateAllCompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueEstimateAll", ex));

        // Working directories: add (folder picker + switch to it), switch (replace queue), remove. All persist.
        // Switching/adding rewrites the queue, so it's disabled while a batch is running.
        var canSwitchDir = this.WhenAnyValue(x => x.IsBatchRunning, running => !running);
        AddCompressWorkingDirCommand = ReactiveCommand.CreateFromTask(AddCompressWorkingDirAsync, canSwitchDir);
        SwitchCompressWorkingDirCommand = ReactiveCommand.Create<string>(SwitchToWorkingDir, canSwitchDir);
        RemoveCompressWorkingDirCommand = ReactiveCommand.Create<string>(RemoveCompressWorkingDir);
        AddCompressWorkingDirCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.AddWorkingDir", ex));
        SwitchCompressWorkingDirCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.SwitchWorkingDir", ex));
        RemoveCompressWorkingDirCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.RemoveWorkingDir", ex));

        // Create a category (subfolder) under the active working area. Enabled only when a working area is active
        // and no batch is running (creating dirs while encoding is fine, but keep it consistent with switching).
        var canAddCategory = this.WhenAnyValue(
            x => x.ActiveWorkingDir, x => x.IsBatchRunning, (area, busy) => !string.IsNullOrEmpty(area) && !busy);
        AddCompressCategoryCommand = ReactiveCommand.Create(AddCompressCategory, canAddCategory);
        AddCompressCategoryCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.AddCategory", ex));

        // Restore the persisted per-file edit/done state BEFORE any queue items are created below.
        _itemStates = CompressItemStateService.Load();
        CompressSegmentStore.PruneOrphans(); // drop abandoned resume chunks from crashed/old sessions

        // Restore saved working directories; auto-switch to the active one (loads its videos into the queue).
        CompressWorkingDirsState dirState = CompressWorkingDirsService.Load();
        foreach (string dir in dirState.Directories)
        {
            CompressWorkingDirectories.Add(dir);
        }
        RefreshCategoryChoices(); // seed the row category dropdown (just the default until an area is active)

        string? startupDir = dirState.Directories
            .FirstOrDefault(d => string.Equals(d, dirState.Active, StringComparison.OrdinalIgnoreCase))
            ?? dirState.Directories.FirstOrDefault();
        if (startupDir != null)
        {
            SwitchToWorkingDir(startupDir);
        }
    }

    // Rebuilds the per-row category dropdown for the ACTIVE working area: an empty "default" sentinel followed by
    // that area's immediate subfolders (categories). Called on switch / startup / active-area removal — the queue
    // is either empty or its items get reset alongside, so a full rebuild never silently drops a live selection.
    private void RefreshCategoryChoices()
    {
        CompressCategoryChoices.Clear();
        CompressCategoryChoices.Add(string.Empty); // index 0 = "default (global / next to source)"

        string? area = _activeWorkingDir;
        if (string.IsNullOrEmpty(area))
        {
            return;
        }

        try
        {
            foreach (string sub in Directory.GetDirectories(area).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                CompressCategoryChoices.Add(sub);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.RefreshCategories", ex); // unreadable/removed area — just the default remains
        }
    }

    // Creates a category subfolder (named in NewCategoryName) directly under the active working area, then appends
    // it to the dropdown (incremental, so existing rows keep their selection). No-op without an active area / name.
    private void AddCompressCategory()
    {
        string? area = _activeWorkingDir;
        if (string.IsNullOrEmpty(area))
        {
            return;
        }

        string name = SanitizeCategoryName(NewCategoryName);
        if (name.Length == 0)
        {
            return;
        }

        try
        {
            string full = Path.Combine(area, name);
            Directory.CreateDirectory(full); // idempotent — creating an existing category just selects it
            if (!CompressCategoryChoices.Any(c => string.Equals(c, full, StringComparison.OrdinalIgnoreCase)))
            {
                CompressCategoryChoices.Add(full);
            }
            NewCategoryName = string.Empty;
            ShowToastAction?.Invoke(LocalizationService.Instance["CompressCategoryCreated"], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.AddCategory", ex);
        }
    }

    // A category is a single subfolder name under the working area: strip characters invalid in a file name
    // (this includes the path separators, so a name can't escape the area or nest).
    private static string SanitizeCategoryName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        string trimmed = name.Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(c, '_');
        }
        return trimmed.Trim();
    }

    private async Task AddCompressWorkingDirAsync()
    {
        if (PickCompressFolderAction == null)
        {
            return;
        }

        string? dir = await PickCompressFolderAction();
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        if (!CompressWorkingDirectories.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase)))
        {
            CompressWorkingDirectories.Add(dir);
        }

        SwitchToWorkingDir(dir); // make it active + load its videos (also persists)
    }

    // Switch the active working directory: replace the queue with that folder's videos.
    private void SwitchToWorkingDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        ActiveWorkingDir = dir;
        ClearCompressQueue();
        RefreshCategoryChoices();        // categories = the new area's subfolders (before items are (re)created)
        AddPathsToQueue(new[] { dir });
        PersistWorkingDirs();
    }

    private void RemoveCompressWorkingDir(string dir)
    {
        if (!CompressWorkingDirectories.Remove(dir))
        {
            return;
        }

        if (string.Equals(dir, _activeWorkingDir, StringComparison.OrdinalIgnoreCase))
        {
            ActiveWorkingDir = null; // active folder removed; leave the queue until another is picked
            RefreshCategoryChoices(); // its categories are gone → dropdown back to just the default
            foreach (CompressQueueItem item in CompressQueue)
            {
                item.SelectedOutputDir = string.Empty; // reset rows whose category belonged to the removed area
            }
        }

        PersistWorkingDirs();
    }

    private void PersistWorkingDirs() => CompressWorkingDirsService.Save(new CompressWorkingDirsState
    {
        Directories = CompressWorkingDirectories.ToList(),
        Active = _activeWorkingDir
    });

    // Re-rotate the tab preview to match an edit just applied to the selected item (from the 進階影片編輯 window).
    internal void RefreshSelectedPreview(CompressQueueItem item)
    {
        if (ReferenceEquals(item, SelectedQueueItem))
        {
            PreviewBitmap = FloatingBitmapConversionHelper.TransformBitmap(_rawPreview, item.Rotation, false, false);
        }
    }

    // Applies any persisted rotation / output name / done-state to a freshly created queue item.
    private void ApplySavedItemState(CompressQueueItem item)
    {
        if (!_itemStates.TryGetValue(item.Path, out CompressItemState? st) || st == null)
        {
            return;
        }

        item.Rotation = st.Rotation;
        if (!string.IsNullOrEmpty(st.OutputName))
        {
            item.OutputName = st.OutputName;
        }
        item.SelectedOutputDir = st.SelectedOutputDir ?? string.Empty;
        item.TrimEnabled = st.TrimEnabled;
        // CompressItemStateService.Load migrates the legacy single TrimStart/End into Segments, so read Segments here.
        item.KeptSegments = st.Segments is { Count: > 0 }
            ? st.Segments.Select(g => new VideoEditSegment((double)g.Start, (double)g.End, (double)g.Speed)).ToList()
            : null;
        item.Crop = st.CropWidth > 0 && st.CropHeight > 0
            ? new VideoEditCrop(st.CropX, st.CropY, st.CropWidth, st.CropHeight)
            : null;
        item.Annotations = st.Annotations is { Count: > 0 }
            ? CompressEditStateMapper.FromState(st.Annotations)
            : null;
        item.AnnotationSurfaceWidth = st.AnnotationSurfaceWidth;
        item.AnnotationSurfaceHeight = st.AnnotationSurfaceHeight;
        item.RedactionTracks = st.RedactionTracks is { Count: > 0 }
            ? CompressEditStateMapper.FromState(st.RedactionTracks)
            : null;
        if (st.Done)
        {
            item.OutputPath = st.OutputPath; // so a restored, already-compressed file can still 開啟資料夾
            item.Status = CompressQueueStatus.Done;
        }
        else if (st.Paused)
        {
            // Show it as paused at its last progress; the user's "繼續" re-encodes from the start.
            item.Progress = st.Progress;
            item.WasPaused = true;
        }
    }

    // Persists a queue item's edit/progress state (rotation, output name, completion, pause) keyed by its path.
    private void PersistItemState(CompressQueueItem item)
    {
        _itemStates[item.Path] = new CompressItemState
        {
            Rotation = item.Rotation,
            OutputName = item.OutputName,
            Done = item.Status == CompressQueueStatus.Done,
            OutputPath = item.OutputPath,
            SelectedOutputDir = item.SelectedOutputDir,
            Paused = item.IsPaused || item.WasPaused,
            Progress = item.Progress,
            TrimEnabled = item.TrimEnabled,
            Segments = item.KeptSegments?
                .Select(s => new TrimSegment { Start = (decimal)s.SourceStart, End = (decimal)s.SourceEnd, Speed = (decimal)s.Speed })
                .ToList(),
            CropX = item.Crop?.X ?? 0,
            CropY = item.Crop?.Y ?? 0,
            CropWidth = item.Crop?.Width ?? 0,
            CropHeight = item.Crop?.Height ?? 0,
            Annotations = item.Annotations is { Count: > 0 }
                ? CompressEditStateMapper.ToState(item.Annotations)
                : null,
            AnnotationSurfaceWidth = item.AnnotationSurfaceWidth,
            AnnotationSurfaceHeight = item.AnnotationSurfaceHeight,
            RedactionTracks = item.RedactionTracks is { Count: > 0 }
                ? CompressEditStateMapper.ToState(item.RedactionTracks)
                : null
        };
        CompressItemStateService.Save(_itemStates);
    }

    // Decodes the selected file's first frame into _rawPreview and shows it rotated to the file's angle.
    private async Task LoadPreviewAsync(CompressQueueItem? item)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        _rawPreview = null;
        PreviewBitmap = null;

        if (item == null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;

        // Make sure we know the source dims (probe now if the add-time probe hasn't finished yet).
        if (item.ProbedWidth <= 0 || item.ProbedHeight <= 0)
        {
            try
            {
                using var sizeProbe = new LibavVideoFramePlayer();
                var info = await sizeProbe.ProbeAsync(item.Path, cts.Token); // single open fills dims (+ duration/fps)
                item.ProbedWidth = info.Width;
                item.ProbedHeight = info.Height;
                if (item.ProbedDuration <= 0) item.ProbedDuration = info.Duration;
                if (item.ProbedFps <= 0) item.ProbedFps = info.Fps;
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.PreviewProbe", ex);
            }
        }

        if (cts != _previewCts || item.ProbedWidth <= 0 || item.ProbedHeight <= 0)
        {
            return; // a newer selection superseded us, or the file is unreadable
        }

        // Thumbnail dims: fit the source into ~360px, even (encoder/sws friendly).
        double scale = Math.Min(1.0, 360.0 / Math.Max(item.ProbedWidth, item.ProbedHeight));
        int tw = Math.Max(2, (int)(item.ProbedWidth * scale)); tw -= tw & 1;
        int th = Math.Max(2, (int)(item.ProbedHeight * scale)); th -= th & 1;

        byte[]? frame = null;
        try
        {
            using var player = new LibavVideoFramePlayer();
            await player.PlayAsync(item.Path, tw, th, 0, 1.0, false, (data, _) =>
            {
                if (frame == null)
                {
                    frame = (byte[])data.Clone(); // grab just the first frame, then stop
                    cts.Cancel();
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected — we cancel right after the first frame
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.PreviewDecode", ex);
        }

        if (frame == null || cts != _previewCts) // null = no frame, or a newer selection superseded us
        {
            return;
        }

        try
        {
            using var sk = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(frame, 0, sk.GetPixels(), Math.Min(frame.Length, tw * th * 4));
            if (FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(sk, out Bitmap? raw, out _))
            {
                _rawPreview = raw;
                PreviewBitmap = FloatingBitmapConversionHelper.TransformBitmap(raw, item.Rotation, false, false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.PreviewBuild", ex);
        }
    }

    private async Task AddCompressFilesAsync()
    {
        if (PickCompressFilesAction == null)
        {
            return;
        }

        int before = CompressQueue.Count;
        AddPathsToQueue(await PickCompressFilesAction());

        // Nothing new added (e.g. the file is already loaded from the active working dir) — say so,
        // so the button never feels dead.
        if (CompressQueue.Count == before)
        {
            CompressStatusText = LocalizationService.Instance["CompressNoNewFiles"];
        }
    }

    /// <summary>Adds files (and the videos inside any folders) to the queue, skipping non-video and dupes.</summary>
    public void AddPathsToQueue(IEnumerable<string>? paths)
    {
        if (paths == null)
        {
            return;
        }

        var existing = new HashSet<string>(
            CompressQueue.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var added = new List<CompressQueueItem>();

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            foreach (string file in ExpandToVideoFiles(path))
            {
                if (existing.Add(file))
                {
                    var item = new CompressQueueItem(file)
                    {
                        SelectedPreset = _currentSettingsChoice, // "current settings" by default
                        StartRequested = StartCompressItem,
                        EstimateRequested = EstimateQueueItem,
                        PauseWarningRequested = ShowPauseNoResumeWarning
                    };
                    ApplySavedItemState(item);
                    // Persist on output-name edits and on pause/resume (fires on the UI thread; survives a
                    // close even without completing). Skip(1) ignores the value emitted on subscribe.
                    item.WhenAnyValue(x => x.OutputName)
                        .Skip(1)
                        .Subscribe(_ => PersistItemState(item));
                    item.WhenAnyValue(x => x.IsPaused)
                        .Skip(1)
                        .Subscribe(_ => PersistItemState(item));
                    // Per-item output folder persists on pick (and when a removed working dir resets it to Default),
                    // so a configure-many-then-run-later batch keeps each row's chosen folder across a restart.
                    item.WhenAnyValue(x => x.SelectedOutputDir)
                        .Skip(1)
                        .Subscribe(_ => PersistItemState(item));
                    // Edits persist and re-estimate (trim/speed/crop/rotation all change the estimated output size).
                    item.WhenAnyValue(x => x.TrimEnabled, x => x.KeptSegments, x => x.Crop, x => x.Rotation)
                        .Skip(1)
                        .Subscribe(_ =>
                        {
                            PersistItemState(item);
                            item.EstimatedText = BuildItemEstimate(item);
                        });
                    // Burn-in layers persist too (they don't change the size estimate meaningfully).
                    item.WhenAnyValue(x => x.Annotations, x => x.RedactionTracks)
                        .Skip(1)
                        .Subscribe(_ => PersistItemState(item));
                    // Re-estimate this item when its output-settings bundle changes.
                    item.WhenAnyValue(x => x.SelectedPreset)
                        .Skip(1)
                        .Subscribe(_ => item.EstimatedText = BuildItemEstimate(item));
                    CompressQueue.Add(item);
                    added.Add(item);
                }
            }
        }

        CompressQueueCount = CompressQueue.Count;

        // Probe each new file (off the UI thread) so its per-row size estimate can be shown.
        foreach (CompressQueueItem item in added)
        {
            _ = ProbeQueueItemAsync(item);
        }
    }

    // Probes a queued file's resolution / fps / duration, then refreshes its "≈ size" estimate.
    private async Task ProbeQueueItemAsync(CompressQueueItem item)
    {
        try
        {
            using var probe = new LibavVideoFramePlayer();
            var info = await probe.ProbeAsync(item.Path); // one open+scan for duration/size/fps (was 3)
            item.ProbedDuration = info.Duration;
            item.ProbedWidth = info.Width;
            item.ProbedHeight = info.Height;
            item.ProbedFps = info.Fps;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeQueueItem", ex);
        }

        item.RaiseEditSummary(); // ProbedDuration now known → the edit summary reflects the real length

        item.EstimatedText = BuildItemEstimate(item);

        // First-frame thumbnail for the picker row (small; reuses the preview decode at a row-sized scale).
        if (item.Thumbnail == null && item.ProbedWidth > 0 && item.ProbedHeight > 0)
        {
            double scale = Math.Min(1.0, 96.0 / Math.Max(item.ProbedWidth, item.ProbedHeight));
            int tw = Math.Max(2, (int)(item.ProbedWidth * scale)); tw -= tw & 1;
            int th = Math.Max(2, (int)(item.ProbedHeight * scale)); th -= th & 1;
            Bitmap? thumb = await DecodeFirstFrameAsync(item.Path, tw, th);
            if (thumb != null)
            {
                item.Thumbnail = thumb;
            }
        }
    }

    // Decodes a video's first frame into an Avalonia Bitmap at the given (even) dimensions, cancelling right after
    // the first frame so it doesn't decode the whole clip. Returns null if there is no frame or the file is unreadable.
    private static async Task<Bitmap?> DecodeFirstFrameAsync(string path, int width, int height)
    {
        using var cts = new CancellationTokenSource();
        byte[]? frame = null;
        try
        {
            using var player = new LibavVideoFramePlayer();
            await player.PlayAsync(path, width, height, 0, 1.0, false, (data, _) =>
            {
                if (frame == null)
                {
                    frame = (byte[])data.Clone();
                    cts.Cancel();
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected — we cancel right after the first frame
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ThumbDecode", ex);
        }

        if (frame == null)
        {
            return null;
        }

        try
        {
            using var sk = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(frame, 0, sk.GetPixels(), Math.Min(frame.Length, width * height * 4));
            return FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(sk, out Bitmap? bmp, out _) ? bmp : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ThumbBuild", ex);
            return null;
        }
    }

    private static IEnumerable<string> ExpandToVideoFiles(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly).Where(IsVideoFile);
            }

            if (File.Exists(path) && IsVideoFile(path))
            {
                return new[] { path };
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.QueueExpand", ex);
        }

        return Array.Empty<string>();
    }

    private static bool IsVideoFile(string path) =>
        CompressVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private void ClearCompressQueue()
    {
        foreach (CompressQueueItem item in CompressQueue)
        {
            item.Dispose();
        }
        CompressQueue.Clear();
        CompressQueueCount = 0;
    }

    private void StartAllCompress()
    {
        // Skip already-completed files so a resumed batch doesn't recompress them (use per-item 啟動 to redo one).
        foreach (CompressQueueItem item in CompressQueue
                     .Where(i => i.CanStart && i.Status != CompressQueueStatus.Done).ToList())
        {
            StartCompressItem(item);
        }
    }

    private void CancelAllCompressQueue() => _batchCts?.Cancel();

    private void EstimateQueueItem(CompressQueueItem item) => _ = EstimateItemSampleAsync(item, notifyTargetSize: true);

    // Accurate per-item estimate: sample-encode a few seconds at the current settings and extrapolate.
    private async Task EstimateItemSampleAsync(CompressQueueItem item, bool notifyTargetSize = false)
    {
        if (item.IsEstimating || item.Status == CompressQueueStatus.Running || item.EffectiveDuration <= 0)
        {
            return;
        }

        CompressSettingsSnapshot snap = BuildSettingsSnapshot(item); // per-item bundle or live settings
        string prefix = LocalizationService.Instance["CompressEstimateLabel"];

        // Target-size mode is already exact (the requested size) — no sample encode needed. Warn on a direct
        // 精算 click so the button doesn't feel dead.
        if (snap.UseTargetSize)
        {
            item.EstimatedText = BuildItemEstimate(item);
            if (notifyTargetSize)
            {
                ShowToastAction?.Invoke(
                    LocalizationService.Instance["CompressEstimateTargetSizeWarning"], ToastSeverity.Info);
            }
            return;
        }

        item.IsEstimating = true;
        item.EstimatedText = $"{prefix}: {LocalizationService.Instance["CompressEstimating"]}";
        try
        {
            long bytes = await EstimateBySampleAsync(item.Path, snap, item.EffectiveKeptRuns(), item.Crop);
            item.EstimatedText = bytes >= 0
                ? $"{prefix}: ≈ {FormatFileSize(bytes)} ({LocalizationService.Instance["CompressEstimateMeasured"]})"
                : BuildItemEstimate(item); // sample failed → fall back to the formula
        }
        finally
        {
            item.IsEstimating = false;
        }
    }

    private async Task EstimateAllAsync()
    {
        // In target-size mode a sample estimate is a no-op (output = the target); warn once instead of per item.
        if (BuildSettingsSnapshot().UseTargetSize)
        {
            ShowToastAction?.Invoke(
                LocalizationService.Instance["CompressEstimateTargetSizeWarning"], ToastSeverity.Info);
            return;
        }

        // Sequential so many files don't spike the CPU with concurrent sample encodes.
        foreach (CompressQueueItem item in CompressQueue.ToList())
        {
            await EstimateItemSampleAsync(item);
        }
    }

    // Dispatches one item: ensures the shared batch context, snapshots settings on the UI thread, and launches
    // a worker (fire-and-forget; tracked via _inFlight). The concurrency cap is enforced by _batchSemaphore.
    private void StartCompressItem(CompressQueueItem item)
    {
        if (item.Status is CompressQueueStatus.Waiting or CompressQueueStatus.Running)
        {
            return;
        }

        if (_batchCts == null || _batchSemaphore == null)
        {
            _batchCts = new CancellationTokenSource();
            _batchSemaphore = new SemaphoreSlim(Math.Clamp(CompressParallelCount, 1, 8));
        }

        CompressSettingsSnapshot snap = BuildSettingsSnapshot(item);         // per-item bundle or live settings
        string ext = "." + EffectiveFormatFor(item).ToLowerInvariant();      // container follows the effective format
        // Per-item folder chosen from the working-dir dropdown overrides the global one; empty = global, else
        // next to source. Captured on the UI thread alongside the other per-run values.
        string outputFolder = string.IsNullOrEmpty(item.SelectedOutputDir) ? CompressOutputFolder : item.SelectedOutputDir;
        bool appendDate = CompressAppendDate;
        // Per-file kept runs captured on the UI thread (whole clip when trim off); re-clamped against the fresh probe.
        IReadOnlyList<(double Start, double End, double Speed)> keptRuns = item.EffectiveKeptRuns();
        // Annotations/redaction burn-in (snapshotted here on the UI thread; null when the item has neither).
        Action<SKBitmap, double>? burnIn = BuildBurnInComposite(
            item.Annotations, item.AnnotationSurfaceWidth, item.AnnotationSurfaceHeight, item.RedactionTracks);
        item.PrepareForStart(_batchCts.Token);
        var progress = new Progress<double>(p => item.Progress = p); // UI thread → callbacks marshal back
        var resumeProgress = new Progress<double>(p => item.ResumePoint = p); // last safe pause checkpoint
        item.ResumePoint = 0;

        _inFlight++;
        IsBatchRunning = true;
        _ = RunQueueItemAsync(item, snap, ext, outputFolder, appendDate, progress, resumeProgress, keptRuns, burnIn);
    }

    private async Task RunQueueItemAsync(
        CompressQueueItem item, CompressSettingsSnapshot snap, string ext, string outputFolder, bool appendDate,
        IProgress<double> progress, IProgress<double> resumeProgress,
        IReadOnlyList<(double Start, double End, double Speed)> keptRuns, Action<SKBitmap, double>? burnInComposite)
    {
        SemaphoreSlim semaphore = _batchSemaphore!;
        CancellationToken token = item.Cts!.Token;
        try
        {
            try
            {
                await semaphore.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                item.Status = CompressQueueStatus.Cancelled;
                CompressSegmentStore.Clear(item.Path); // cancelled: discard any resume chunks
                return;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(item.Path))
                {
                    item.Status = CompressQueueStatus.Failed;
                    return;
                }

                item.Gate = new ManualResetEventSlim(true);
                item.Status = CompressQueueStatus.Running;
                PersistItemState(item); // clear any stale "paused" marker now that it's actually encoding

                double duration = await ProbeInputDurationAsync(item.Path);

                // Re-clamp the captured kept runs against the freshly probed duration (whole file when empty).
                var runs = keptRuns
                    .Select(r => (Start: Math.Clamp(r.Start, 0, duration), End: Math.Clamp(r.End, 0, duration),
                        Speed: r.Speed > 0 ? r.Speed : 1.0))
                    .Where(r => r.End > r.Start + 0.001)
                    .OrderBy(r => r.Start)
                    .ToList();
                if (runs.Count == 0 && duration > 0)
                {
                    runs.Add((0, duration, 1.0));
                }
                // Output span honors per-run speed (a 2× run is half as long); drives the target-size bitrate.
                double outputDuration = runs.Sum(r => (r.End - r.Start) / (r.Speed > 0 ? r.Speed : 1.0));
                bool multiSegment = runs.Count > 1;
                bool hasSpeed = runs.Any(r => Math.Abs(r.Speed - 1.0) > 0.001);
                VideoEditCrop? crop = item.Crop;

                // Resumable segmented CRF only handles one contiguous, full-speed, un-cropped, un-annotated span;
                // anything else (target-size / multi-segment / speed / crop / burn-in) uses the whole-file path below.
                item.SupportsResume = !snap.UseTargetSize && duration > 0 && !multiSegment && !hasSpeed && crop == null
                    && burnInComposite == null;
                int targetKbps = 0;
                if (snap.UseTargetSize)
                {
                    if (duration <= 0 || outputDuration <= 0)
                    {
                        item.Status = CompressQueueStatus.Failed;
                        return;
                    }
                    targetKbps = ComputeTargetVideoBitrateKbps((double)snap.TargetSizeMB, outputDuration);
                }

                string outputPath = BuildBatchOutputPath(
                    item.Path, item.OutputName, outputFolder, ext, appendDate, DateTime.Now);
                try
                {
                    string? finalDir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(finalDir))
                    {
                        Directory.CreateDirectory(finalDir); // includes any user-typed subfolder
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("Compress.QueueOutDir", ex);
                }

                bool ok = await EncodeOneFileAsync(
                    item.Path, outputPath, snap, targetKbps, duration, progress, token, item.Gate, item.Rotation,
                    resumeProgress, runs, crop, burnInComposite);
                if (ok)
                {
                    item.OutputPath = outputPath; // record the produced file for the 開啟資料夾 button
                }
                item.Status = ok ? CompressQueueStatus.Done : CompressQueueStatus.Failed;
                if (ok)
                {
                    PersistItemState(item); // remember completion so a resumed batch skips this file
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = CompressQueueStatus.Cancelled;
                CompressSegmentStore.Clear(item.Path); // cancelled: discard any resume chunks
            }
            catch (Exception ex)
            {
                AppLog.Error("Compress.QueueItem", ex);
                item.Status = CompressQueueStatus.Failed;
            }
            finally
            {
                item.Gate?.Dispose();
                item.Gate = null;
                semaphore.Release();
            }
        }
        finally
        {
            item.Cts?.Dispose();
            item.Cts = null;
            OnWorkerDone();
        }
    }

    // Runs on the UI thread (worker continuation). When the last worker finishes, tears down the batch context.
    private void OnWorkerDone()
    {
        _inFlight--;
        if (_inFlight > 0)
        {
            return;
        }

        _inFlight = 0;
        _batchSemaphore?.Dispose();
        _batchSemaphore = null;
        _batchCts?.Dispose();
        _batchCts = null;
        IsBatchRunning = false;

        int done = CompressQueue.Count(i => i.Status == CompressQueueStatus.Done);
        CompressStatusText = $"{LocalizationService.Instance["CompressQueueComplete"]}  ({done}/{CompressQueue.Count})";

        // Batch finished — release the encode/decode working buffers accumulated across the run.
        ProcessMemoryTrimService.RequestIdleTrimAsync("after-batch-compress").Forget("MemoryTrim.BatchCompress");
    }
}
