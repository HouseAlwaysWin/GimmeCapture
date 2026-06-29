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

// Batch queue for the Compress tab: drop / add multiple files (or a folder) and compress them all with the
// current settings, each output auto-saved next to its source. Shares the encode core + cancel/pause/progress
// state with the single-file path (see MainWindowViewModel.VideoCompress.cs).
public partial class MainWindowViewModel
{
    public enum CompressQueueStatus
    {
        Queued,
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
        }

        public string Path { get; }
        public string FileName { get; }

        private CompressQueueStatus _status = CompressQueueStatus.Queued;
        public CompressQueueStatus Status
        {
            get => _status;
            set => this.RaiseAndSetIfChanged(ref _status, value);
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }
    }

    // Source containers the batch will accept (mirrors the single-file picker filter).
    private static readonly string[] CompressVideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv"];

    public ObservableCollection<CompressQueueItem> CompressQueue { get; } = new();

    // View-set pickers (multi-file + folder), mirroring PickCompressInputAction.
    public Func<Task<IReadOnlyList<string>>>? PickCompressFilesAction { get; set; }
    public Func<Task<string?>>? PickCompressFolderAction { get; set; }

    // Mirrors CompressQueue.Count for command CanExecute (ObservableCollection.Count isn't directly observable).
    private int _compressQueueCount;
    public int CompressQueueCount
    {
        get => _compressQueueCount;
        private set => this.RaiseAndSetIfChanged(ref _compressQueueCount, value);
    }

    public ReactiveCommand<Unit, Unit> AddCompressFilesCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> AddCompressFolderCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearCompressQueueCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CompressQueueCommand { get; private set; } = null!;

    private void InitializeCompressBatch()
    {
        var notBusy = this.WhenAnyValue(x => x.IsCompressing, busy => !busy);
        var canRunQueue = this.WhenAnyValue(
            x => x.CompressQueueCount, x => x.IsCompressing, (count, busy) => count > 0 && !busy);

        AddCompressFilesCommand = ReactiveCommand.CreateFromTask(AddCompressFilesAsync, notBusy);
        AddCompressFolderCommand = ReactiveCommand.CreateFromTask(AddCompressFolderAsync, notBusy);
        ClearCompressQueueCommand = ReactiveCommand.Create(ClearCompressQueue, canRunQueue);
        CompressQueueCommand = ReactiveCommand.CreateFromTask(CompressQueueAsync, canRunQueue);

        AddCompressFilesCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueAddFiles", ex));
        AddCompressFolderCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueAddFolder", ex));
        ClearCompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueClear", ex));
        CompressQueueCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.QueueRun", ex));
    }

    private async Task AddCompressFilesAsync()
    {
        if (PickCompressFilesAction == null)
        {
            return;
        }

        IReadOnlyList<string> files = await PickCompressFilesAction();
        AddPathsToQueue(files);
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

    /// <summary>
    /// Adds files (and the videos inside any folders) to the queue, skipping non-video and duplicate paths.
    /// Called by the pickers and by the view's drag-drop handler.
    /// </summary>
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
                    var item = new CompressQueueItem(file);
                    SetItemStatus(item, CompressQueueStatus.Queued);
                    CompressQueue.Add(item);
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

    private void SetItemStatus(CompressQueueItem item, CompressQueueStatus status)
    {
        item.Status = status;
        item.StatusText = LocalizationService.Instance[status switch
        {
            CompressQueueStatus.Running => "CompressQueueRunning",
            CompressQueueStatus.Done => "CompressQueueDone",
            CompressQueueStatus.Failed => "CompressQueueFailed",
            CompressQueueStatus.Cancelled => "CompressQueueCancelled",
            _ => "CompressQueueQueued"
        }];
    }

    private async Task CompressQueueAsync()
    {
        if (IsCompressing)
        {
            return;
        }

        // Re-run anything not already done (Queued or previously Failed); leave completed items alone.
        var pending = CompressQueue
            .Where(i => i.Status is CompressQueueStatus.Queued or CompressQueueStatus.Failed or CompressQueueStatus.Cancelled)
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        CompressSettingsSnapshot snap = BuildSettingsSnapshot();
        string ext = "." + SelectedCompressFormat.ToLowerInvariant();

        IsCompressing = true;
        CompressProgress = 0;
        IsPaused = false;
        _compressCts?.Dispose();
        _compressCts = new CancellationTokenSource();
        CancellationToken token = _compressCts.Token;
        _compressPauseGate?.Dispose();
        _compressPauseGate = new ManualResetEventSlim(true);

        var encodeProgress = new Progress<double>(p => CompressProgress = p);
        int done = 0;

        try
        {
            for (int i = 0; i < pending.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                CompressQueueItem item = pending[i];

                CompressProgress = 0;
                CompressStatusText = $"({i + 1}/{pending.Count})  {item.FileName}";
                SetItemStatus(item, CompressQueueStatus.Running);

                if (!File.Exists(item.Path))
                {
                    SetItemStatus(item, CompressQueueStatus.Failed);
                    continue;
                }

                double duration = await ProbeInputDurationAsync(item.Path);
                int targetKbps = 0;
                if (snap.UseTargetSize)
                {
                    if (duration <= 0)
                    {
                        SetItemStatus(item, CompressQueueStatus.Failed); // can't hit a target size without a length
                        continue;
                    }
                    targetKbps = ComputeTargetVideoBitrateKbps((double)snap.TargetSizeMB, duration);
                }

                string outputPath = BuildCompressOutputPath(item.Path, ext);
                try
                {
                    bool ok = await EncodeOneFileAsync(
                        item.Path, outputPath, snap, targetKbps, duration, encodeProgress, token, _compressPauseGate);
                    SetItemStatus(item, ok ? CompressQueueStatus.Done : CompressQueueStatus.Failed);
                    if (ok)
                    {
                        done++;
                    }
                }
                catch (OperationCanceledException)
                {
                    SetItemStatus(item, CompressQueueStatus.Cancelled);
                    throw;
                }
            }

            CompressStatusText = $"{LocalizationService.Instance["CompressQueueComplete"]}  ({done}/{pending.Count})";
            ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            foreach (CompressQueueItem item in pending)
            {
                if (item.Status is CompressQueueStatus.Running or CompressQueueStatus.Queued)
                {
                    SetItemStatus(item, CompressQueueStatus.Cancelled);
                }
            }
            CompressStatusText = LocalizationService.Instance["StatusCompressCancelled"];
            ShowToastAction?.Invoke(CompressStatusText, ToastSeverity.Info);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.QueueRun", ex);
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
        }
    }
}
