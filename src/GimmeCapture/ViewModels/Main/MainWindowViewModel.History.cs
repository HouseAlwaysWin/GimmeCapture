using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private readonly ClipboardService _historyClipboardService = new();
    private IReadOnlyList<HistoryItemViewModel> _allHistoryItems = [];

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

        RefreshHistoryCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Refresh", ex));
        ClearHistoryCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Clear", ex));
        OpenHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Open", ex));
        CopyHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Copy", ex));
        RevealHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Reveal", ex));
        RemoveHistoryItemCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("History.Remove", ex));
    }

    /// <summary>Loads the history index and rebuilds the (filtered) bound collection. Called when the tab is shown.</summary>
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
        IEnumerable<HistoryItemViewModel> query = _allHistoryItems;
        if (!string.IsNullOrWhiteSpace(HistorySearchText))
        {
            var term = HistorySearchText.Trim();
            query = query.Where(i => i.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            HistoryItems.Add(item);
        }
        IsHistoryEmpty = HistoryItems.Count == 0;
    }

    private async Task ClearHistoryAsync()
    {
        await CaptureHistory.ClearAsync();
        await LoadHistoryAsync();
    }

    private async Task RemoveHistoryItemAsync(HistoryItemViewModel item)
    {
        if (item == null) return;
        await CaptureHistory.RemoveAsync(item.Id);
        await LoadHistoryAsync();
    }

    private async Task CopyHistoryItemAsync(HistoryItemViewModel item)
    {
        // Image copy only; videos can be opened/revealed instead.
        if (item == null || item.IsVideo || !System.IO.File.Exists(item.FilePath)) return;
        try
        {
            using var bitmap = new Bitmap(item.FilePath);
            await _historyClipboardService.CopyImageAsync(bitmap);
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
