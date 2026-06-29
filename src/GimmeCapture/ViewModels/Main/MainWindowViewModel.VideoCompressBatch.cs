using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

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
            StartCommand = ReactiveCommand.Create(() => StartRequested?.Invoke(this), this.WhenAnyValue(x => x.CanStart));
            PauseCommand = ReactiveCommand.Create(Pause, this.WhenAnyValue(x => x.ShowPause));
            ResumeCommand = ReactiveCommand.Create(Resume, this.WhenAnyValue(x => x.ShowResume));
            CancelCommand = ReactiveCommand.Create(Cancel, this.WhenAnyValue(x => x.CanCancel));
            UpdateStatusText();
        }

        public string Path { get; }
        public string FileName { get; }

        // Set by the view model so the per-item Start button can ask it to dispatch a worker.
        internal Action<CompressQueueItem>? StartRequested;

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

    public Func<Task<IReadOnlyList<string>>>? PickCompressFilesAction { get; set; }
    public Func<Task<string?>>? PickCompressFolderAction { get; set; }

    // True while any queue item is waiting/running. IsBusy = single-file OR batch, so the two never overlap.
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

    public bool IsBusy => IsCompressing || IsBatchRunning;

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

    public ReactiveCommand<Unit, Unit> AddCompressFilesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddCompressFolderCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearCompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelAllCompressQueueCommand { get; private set; } = null!;

    private void InitializeCompressBatch()
    {
        // Add: not while a single-file compress runs (adding mid-batch is fine). Clear: only when fully idle.
        var notSingle = this.WhenAnyValue(x => x.IsCompressing, single => !single);
        var canStartAll = this.WhenAnyValue(
            x => x.CompressQueueCount, x => x.IsCompressing, (count, single) => count > 0 && !single);
        var canClear = this.WhenAnyValue(
            x => x.CompressQueueCount, x => x.IsBusy, (count, busy) => count > 0 && !busy);

        AddCompressFilesCommand = ReactiveCommand.CreateFromTask(AddCompressFilesAsync, notSingle);
        AddCompressFolderCommand = ReactiveCommand.CreateFromTask(AddCompressFolderAsync, notSingle);
        ClearCompressQueueCommand = ReactiveCommand.Create(ClearCompressQueue, canClear);
        CompressQueueCommand = ReactiveCommand.Create(StartAllCompress, canStartAll);
        CancelAllCompressQueueCommand = ReactiveCommand.Create(
            CancelAllCompressQueue, this.WhenAnyValue(x => x.IsBatchRunning));

        AddCompressFilesCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueAddFiles", ex));
        AddCompressFolderCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueAddFolder", ex));
        ClearCompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueClear", ex));
        CompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueRun", ex));
        CancelAllCompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueCancelAll", ex));
    }

    private async Task AddCompressFilesAsync()
    {
        if (PickCompressFilesAction == null)
        {
            return;
        }

        AddPathsToQueue(await PickCompressFilesAction());
    }

    private async Task AddCompressFolderAsync()
    {
        if (PickCompressFolderAction == null)
        {
            return;
        }

        string? folder = await PickCompressFolderAction();
        if (!string.IsNullOrEmpty(folder))
        {
            AddPathsToQueue(new[] { folder });
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
                    CompressQueue.Add(new CompressQueueItem(file) { StartRequested = StartCompressItem });
                }
            }
        }

        CompressQueueCount = CompressQueue.Count;
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

    // Dispatches one item: ensures the shared batch context, snapshots settings on the UI thread, and launches
    // a worker (fire-and-forget; tracked via _inFlight). The concurrency cap is enforced by _batchSemaphore.
    private void StartCompressItem(CompressQueueItem item)
    {
        if (item.Status is CompressQueueStatus.Waiting or CompressQueueStatus.Running || IsCompressing)
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
        item.PrepareForStart(_batchCts.Token);
        var progress = new Progress<double>(p => item.Progress = p); // UI thread → callbacks marshal back

        _inFlight++;
        IsBatchRunning = true;
        _ = RunQueueItemAsync(item, snap, ext, progress);
    }

    private async Task RunQueueItemAsync(
        CompressQueueItem item, CompressSettingsSnapshot snap, string ext, IProgress<double> progress)
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

                string outputPath = BuildCompressOutputPath(item.Path, ext);
                bool ok = await EncodeOneFileAsync(
                    item.Path, outputPath, snap, targetKbps, duration, progress, token, item.Gate);
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
