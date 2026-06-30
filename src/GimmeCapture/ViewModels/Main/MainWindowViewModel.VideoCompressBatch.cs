using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
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

    public sealed class CompressQueueItem : ReactiveObject
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
            set => this.RaiseAndSetIfChanged(ref _rotation, value);
        }

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

        public ReactiveCommand<Unit, Unit> StartCommand { get; }
        public ReactiveCommand<Unit, Unit> PauseCommand { get; }
        public ReactiveCommand<Unit, Unit> ResumeCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        public ReactiveCommand<Unit, Unit> EstimateCommand { get; }

        // Prepares the item just before a worker is launched (caller has ensured a batch context exists).
        internal void PrepareForStart(CancellationToken batchToken)
        {
            Cts?.Dispose();
            Cts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);
            IsPaused = false;
            Progress = 0;
            Status = CompressQueueStatus.Waiting;
        }

        private void Pause()
        {
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
            StatusText = LocalizationService.Instance[Status switch
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

    private string? _activeWorkingDir;
    // The working directory whose videos are currently loaded in the queue (auto-loaded again next launch).
    public string? ActiveWorkingDir
    {
        get => _activeWorkingDir;
        private set => this.RaiseAndSetIfChanged(ref _activeWorkingDir, value);
    }

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

        // Load the first-frame preview when the selected file changes (and refresh the editable angle).
        this.WhenAnyValue(x => x.SelectedQueueItem).Subscribe(item =>
        {
            this.RaisePropertyChanged(nameof(SelectedRotation));
            _ = LoadPreviewAsync(item);
        });

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

        // Restore saved working directories; auto-switch to the active one (loads its videos into the queue).
        CompressWorkingDirsState dirState = CompressWorkingDirsService.Load();
        foreach (string dir in dirState.Directories)
        {
            CompressWorkingDirectories.Add(dir);
        }
        string? startupDir = dirState.Directories
            .FirstOrDefault(d => string.Equals(d, dirState.Active, StringComparison.OrdinalIgnoreCase))
            ?? dirState.Directories.FirstOrDefault();
        if (startupDir != null)
        {
            SwitchToWorkingDir(startupDir);
        }
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
        }

        PersistWorkingDirs();
    }

    private void PersistWorkingDirs() => CompressWorkingDirsService.Save(new CompressWorkingDirsState
    {
        Directories = CompressWorkingDirectories.ToList(),
        Active = _activeWorkingDir
    });

    // The selected file's rotation, editable as a number (snaps to 0/90/180/270). Nullable so a transient
    // empty field can't crash the binding; the NumericUpDownFix behavior restores a value on empty.
    public decimal? SelectedRotation
    {
        get => SelectedQueueItem?.Rotation ?? 0;
        set
        {
            if (value.HasValue)
            {
                ApplyRotation((int)value.Value);
            }
        }
    }

    // Snaps any angle to the nearest 90° (0/90/180/270), applies it to the selected file, and re-rotates the preview.
    private void ApplyRotation(int degrees)
    {
        CompressQueueItem? item = SelectedQueueItem;
        if (item == null)
        {
            return;
        }

        int snapped = ((int)Math.Round(degrees / 90.0, MidpointRounding.AwayFromZero) * 90 % 360 + 360) % 360;
        item.Rotation = snapped;
        PreviewBitmap = FloatingBitmapConversionHelper.TransformBitmap(_rawPreview, snapped, false, false);
        this.RaisePropertyChanged(nameof(SelectedRotation));
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
                var size = await sizeProbe.ProbeVideoSizeAsync(item.Path, cts.Token);
                if (size is { } s)
                {
                    item.ProbedWidth = s.Width;
                    item.ProbedHeight = s.Height;
                }
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
                        StartRequested = StartCompressItem,
                        EstimateRequested = EstimateQueueItem
                    };
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
            item.ProbedDuration = await probe.ProbeDurationSecondsAsync(item.Path) ?? 0;
            var size = await probe.ProbeVideoSizeAsync(item.Path);
            if (size is { } s)
            {
                item.ProbedWidth = s.Width;
                item.ProbedHeight = s.Height;
            }
            item.ProbedFps = await Task.Run(() => LibavClipExporter.ProbeFps(item.Path));
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.ProbeQueueItem", ex);
        }

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
        CompressQueue.Clear();
        CompressQueueCount = 0;
    }

    private void StartAllCompress()
    {
        foreach (CompressQueueItem item in CompressQueue.Where(i => i.CanStart).ToList())
        {
            StartCompressItem(item);
        }
    }

    private void CancelAllCompressQueue() => _batchCts?.Cancel();

    private void EstimateQueueItem(CompressQueueItem item) => _ = EstimateItemSampleAsync(item);

    // Accurate per-item estimate: sample-encode a few seconds at the current settings and extrapolate.
    private async Task EstimateItemSampleAsync(CompressQueueItem item)
    {
        if (item.IsEstimating || item.Status == CompressQueueStatus.Running || item.ProbedDuration <= 0)
        {
            return;
        }

        CompressSettingsSnapshot snap = BuildSettingsSnapshot();
        string prefix = LocalizationService.Instance["CompressEstimateLabel"];

        // Target-size mode is already exact (the requested size) — no sample encode needed.
        if (snap.UseTargetSize)
        {
            item.EstimatedText = BuildItemEstimate(item);
            return;
        }

        item.IsEstimating = true;
        item.EstimatedText = $"{prefix}: {LocalizationService.Instance["CompressEstimating"]}";
        try
        {
            long bytes = await EstimateBySampleAsync(item.Path, snap, item.ProbedDuration);
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

        CompressSettingsSnapshot snap = BuildSettingsSnapshot();
        string ext = "." + SelectedCompressFormat.ToLowerInvariant();
        string outputFolder = CompressOutputFolder; // captured on the UI thread; empty = next to source
        bool appendDate = CompressAppendDate;
        item.PrepareForStart(_batchCts.Token);
        var progress = new Progress<double>(p => item.Progress = p); // UI thread → callbacks marshal back

        _inFlight++;
        IsBatchRunning = true;
        _ = RunQueueItemAsync(item, snap, ext, outputFolder, appendDate, progress);
    }

    private async Task RunQueueItemAsync(
        CompressQueueItem item, CompressSettingsSnapshot snap, string ext, string outputFolder, bool appendDate,
        IProgress<double> progress)
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

                double duration = await ProbeInputDurationAsync(item.Path);
                int targetKbps = 0;
                if (snap.UseTargetSize)
                {
                    if (duration <= 0)
                    {
                        item.Status = CompressQueueStatus.Failed;
                        return;
                    }
                    targetKbps = ComputeTargetVideoBitrateKbps((double)snap.TargetSizeMB, duration);
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
                    item.Path, outputPath, snap, targetKbps, duration, progress, token, item.Gate, item.Rotation);
                item.Status = ok ? CompressQueueStatus.Done : CompressQueueStatus.Failed;
            }
            catch (OperationCanceledException)
            {
                item.Status = CompressQueueStatus.Cancelled;
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
    }
}
