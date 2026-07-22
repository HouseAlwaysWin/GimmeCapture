using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private readonly IClipboardService _clipboardService = new ClipboardService();
    private IReadOnlyList<HistoryItemViewModel> _allHistoryItems = [];
    private bool _historyViewActive;

    /// <summary>Filtered, newest-first history items bound to the History tab grid.</summary>
    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; } = new();

    private string _historySearchText = string.Empty;
    public string HistorySearchText
    {
        get => _historySearchText;
        set
        {
            if (_historySearchText != value)
            {
                this.RaiseAndSetIfChanged(ref _historySearchText, value);
                ApplyHistoryFilter();
            }
        }
    }

    private bool _isHistoryEmpty = true;
    public bool IsHistoryEmpty
    {
        get => _isHistoryEmpty;
        private set => this.RaiseAndSetIfChanged(ref _isHistoryEmpty, value);
    }

    // History category tabs: screenshots (images) vs recordings (videos). Entries still record how they were
    // produced (CaptureHistorySource: output/export vs plain clipboard copy) and both kinds appear here — the
    // tabs simply group by what the capture IS rather than how it was produced, which is how users look for it.
    private CaptureHistoryKind _historyKindFilter = CaptureHistoryKind.Image;
    public CaptureHistoryKind HistoryKindFilter
    {
        get => _historyKindFilter;
        set
        {
            if (_historyKindFilter != value)
            {
                this.RaiseAndSetIfChanged(ref _historyKindFilter, value);
                this.RaisePropertyChanged(nameof(IsHistoryScreenshotTab));
                this.RaisePropertyChanged(nameof(IsHistoryRecordingTab));
                this.RaisePropertyChanged(nameof(HistoryCategoryIndex));
                ApplyHistoryFilter();
            }
        }
    }

    /// <summary>Two-way bound to the "screenshot" tab toggle.</summary>
    public bool IsHistoryScreenshotTab
    {
        get => HistoryKindFilter == CaptureHistoryKind.Image;
        set { if (value) HistoryKindFilter = CaptureHistoryKind.Image; }
    }

    /// <summary>Two-way bound to the "recording" tab toggle.</summary>
    public bool IsHistoryRecordingTab
    {
        get => HistoryKindFilter == CaptureHistoryKind.Video;
        set { if (value) HistoryKindFilter = CaptureHistoryKind.Video; }
    }

    /// <summary>Two-way bound to the history category TabStrip (0 = screenshots, 1 = recordings).</summary>
    public int HistoryCategoryIndex
    {
        get => HistoryKindFilter == CaptureHistoryKind.Video ? 1 : 0;
        set => HistoryKindFilter = value == 1 ? CaptureHistoryKind.Video : CaptureHistoryKind.Image;
    }

    /// <summary>
    /// Two-way bound to the sort ComboBox, whose items are declared in the same order in the view:
    /// 0 = newest first (default), 1 = oldest first, 2 = name A→Z, 3 = name Z→A.
    /// </summary>
    private int _historySortIndex;
    public int HistorySortIndex
    {
        get => _historySortIndex;
        set
        {
            if (_historySortIndex != value)
            {
                this.RaiseAndSetIfChanged(ref _historySortIndex, value);
                ApplyHistoryFilter();
            }
        }
    }

    /// <summary>Applies the selected sort. Kept separate from filtering so both stay readable.</summary>
    private IEnumerable<HistoryItemViewModel> SortHistory(IEnumerable<HistoryItemViewModel> items)
    {
        return HistorySortIndex switch
        {
            1 => items.OrderBy(i => i.CapturedAtUtc),
            // File names share a long "GimmeCapture_<guid>" prefix, so compare them the way a file manager does
            // (case-insensitive) rather than by ordinal, which would scatter otherwise-adjacent names.
            2 => items.OrderBy(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            3 => items.OrderByDescending(i => i.FileName, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(i => i.CapturedAtUtc),
        };
    }

    public ReactiveCommand<Unit, Unit> RefreshHistoryCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearHistoryCommand { get; private set; } = null!;
    public ReactiveCommand<HistoryItemViewModel, Unit> OpenHistoryItemCommand { get; private set; } = null!;
    public ReactiveCommand<HistoryItemViewModel, Unit> CopyHistoryItemCommand { get; private set; } = null!;
    public ReactiveCommand<HistoryItemViewModel, Unit> RevealHistoryItemCommand { get; private set; } = null!;
    public ReactiveCommand<HistoryItemViewModel, Unit> RemoveHistoryItemCommand { get; private set; } = null!;

    private void InitializeHistoryCommands()
    {
        RefreshHistoryCommand = ReactiveCommand.CreateFromTask(LoadHistoryAsync);
        ClearHistoryCommand = ReactiveCommand.CreateFromTask(ClearHistoryAsync);
        OpenHistoryItemCommand = ReactiveCommand.Create<HistoryItemViewModel>(OpenHistoryItem);
        CopyHistoryItemCommand = ReactiveCommand.CreateFromTask<HistoryItemViewModel>(CopyHistoryItemAsync);
        RevealHistoryItemCommand = ReactiveCommand.Create<HistoryItemViewModel>(
            item => { if (item != null) FileLocationService.RevealInFileExplorer(item.FilePath); });
        RemoveHistoryItemCommand = ReactiveCommand.CreateFromTask<HistoryItemViewModel>(RemoveHistoryItemAsync);

        CaptureHistory.Changed += OnCaptureHistoryChanged;

        RefreshHistoryCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Refresh", ex));
        ClearHistoryCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Clear", ex));
        OpenHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Open", ex));
        CopyHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Copy", ex));
        RevealHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Reveal", ex));
        RemoveHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Remove", ex));
    }

    /// <summary>Called by the History tab when it appears/disappears. While active, the panel auto-refreshes on new captures.</summary>
    public void SetHistoryViewActive(bool active)
    {
        _historyViewActive = active;
        if (active)
        {
            LoadHistoryAsync().Forget("History.LoadOnActivate");
        }
    }

    private void OnCaptureHistoryChanged()
    {
        if (!_historyViewActive) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LoadHistoryAsync().Forget("History.AutoRefresh"));
    }

    /// <summary>Loads the history index and rebuilds the (filtered) bound collection.</summary>
    public async Task LoadHistoryAsync()
    {
        var items = await CaptureHistory.GetItemsAsync();
        foreach (var old in _allHistoryItems)
        {
            old.Dispose();
        }
        _allHistoryItems = items.Select(i => new HistoryItemViewModel(
            i,
            OpenHistoryItemCommand,
            CopyHistoryItemCommand,
            RevealHistoryItemCommand,
            RemoveHistoryItemCommand)).ToList();
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        HistoryItems.Clear();
        bool wantVideo = HistoryKindFilter == CaptureHistoryKind.Video;
        IEnumerable<HistoryItemViewModel> query = _allHistoryItems.Where(i => i.IsVideo == wantVideo);
        if (!string.IsNullOrWhiteSpace(HistorySearchText))
        {
            var term = HistorySearchText.Trim();
            query = query.Where(i => i.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in SortHistory(query))
        {
            HistoryItems.Add(item);
        }
        IsHistoryEmpty = HistoryItems.Count == 0;
    }

    private async Task ClearHistoryAsync()
    {
        if (HistoryItems.Count == 0 && _allHistoryItems.Count == 0) return;

        // Clear deletes every saved file too (high blast radius) — confirm first. Yes/No only.
        var title = LocalizationService.Instance["HistoryClearTitle"];
        var message = LocalizationService.Instance["HistoryClearConfirm"];
        if (ConfirmYesNoAction != null)
        {
            if (!await ConfirmYesNoAction(title, message)) return;
        }
        else if (ConfirmAction != null)
        {
            if (!await ConfirmAction(title, message, false)) return;
        }

        // Refresh comes from CaptureHistory.Changed.
        await CaptureHistory.ClearAsync(deleteSourceFiles: true);
    }

    private async Task RemoveHistoryItemAsync(HistoryItemViewModel item)
    {
        if (item == null) return;

        // Remove deletes the underlying saved file, so confirm first (irreversible). Yes/No only.
        var title = LocalizationService.Instance["HistoryRemoveTitle"];
        var message = string.Format(LocalizationService.Instance["HistoryRemoveConfirm"], item.FileName);
        if (ConfirmYesNoAction != null)
        {
            if (!await ConfirmYesNoAction(title, message)) return;
        }
        else if (ConfirmAction != null)
        {
            if (!await ConfirmAction(title, message, false)) return;
        }

        // Refresh comes from CaptureHistory.Changed.
        await CaptureHistory.RemoveAsync(item.Id, deleteSourceFile: true);
    }

    private async Task CopyHistoryItemAsync(HistoryItemViewModel item)
    {
        // Image copy only; videos can be opened/revealed instead.
        if (item == null || item.IsVideo || !System.IO.File.Exists(item.FilePath)) return;
        try
        {
            using var bitmap = new Bitmap(item.FilePath);
            await _clipboardService.CopyImageAsync(bitmap);
            SetStatus("StatusCopied");
        }
        catch (Exception ex)
        {
            AppLog.Warning("History.CopyImage", ex);
        }
    }

    private void OpenHistoryItem(HistoryItemViewModel item)
    {
        if (item == null || !System.IO.File.Exists(item.FilePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warning("History.OpenFile", ex);
        }
    }
}
