using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsHistoryTab : UserControl
{
    // A card's width plus its margins: Width="210" Margin="6" in the card template.
    private const double CardSlotWidth = 222;

    private readonly ObservableCollection<HistoryRow> _rows = new();
    private MainWindowViewModel? _viewModel;
    private int _columns; // 0 until the grid has been laid out once
    private bool _rebuildQueued;

    public SettingsHistoryTab()
    {
        InitializeComponent();
        this.FindControl<ItemsControl>("HistoryRowsHost")!.ItemsSource = _rows;
        this.FindControl<ScrollViewer>("HistoryScroller")!.SizeChanged += (_, e) => OnGridWidthChanged(e.NewSize.Width);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.HistoryItems.CollectionChanged += OnHistoryItemsChanged;
            QueueRowRebuild();
        }

        // Activating loads the list now and keeps it auto-refreshing while the tab is shown.
        _viewModel?.SetHistoryViewActive(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel != null)
        {
            _viewModel.HistoryItems.CollectionChanged -= OnHistoryItemsChanged;
            _viewModel.SetHistoryViewActive(false);
        }

        _viewModel = null;
    }

    private void OnHistoryItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRowRebuild();

    private void OnGridWidthChanged(double width)
    {
        int columns = Math.Max(1, (int)(width / CardSlotWidth));
        if (columns != _columns)
        {
            _columns = columns;
            QueueRowRebuild();
        }
    }

    // One rebuild for a whole batch of list changes: a refresh can move many items at once.
    private void QueueRowRebuild()
    {
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        Dispatcher.UIThread.Post(RebuildRows);
    }

    private void RebuildRows()
    {
        _rebuildQueued = false;
        if (_viewModel == null || _columns == 0)
        {
            return;
        }

        ObservableListReconciler.Apply(_rows, HistoryRow.Build(_viewModel.HistoryItems, _columns, _rows));
    }
}
