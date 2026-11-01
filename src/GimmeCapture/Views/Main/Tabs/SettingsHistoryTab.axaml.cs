using System;
using System.Collections.Generic;
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
    // The rows the virtualizing list has built cards for, by container. Their thumbnails are decoded while the tab is in
    // the window and let go of as a row scrolls away or the tab leaves it.
    private readonly Dictionary<Control, HistoryRow> _rowsBuilt = new();
    private MainWindowViewModel? _viewModel;
    private int _columns; // 0 until the grid has been laid out once
    private bool _rebuildQueued;
    private bool _showingThumbnails;

    public SettingsHistoryTab()
    {
        InitializeComponent();
        var rowsHost = this.FindControl<ItemsControl>("HistoryRowsHost")!;
        rowsHost.ItemsSource = _rows;
        rowsHost.ContainerPrepared += OnRowContainerPrepared;
        rowsHost.ContainerClearing += (_, e) => ForgetRowContainer(e.Container);
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

        // Rows built before the tab last left the window keep their cards and get no new containers.
        _showingThumbnails = true;
        foreach (var row in _rowsBuilt.Values)
        {
            ShowThumbnails(row);
        }
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
        foreach (var row in _rowsBuilt.Values)
        {
            HideThumbnails(row);
        }

        _showingThumbnails = false;
    }

    private void OnRowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // A container the list hands to another row may come without a clearing first. The row comes by index: the
        // panel registers the container only after this, so ItemFromContainer has nothing for it yet.
        ForgetRowContainer(e.Container);
        if (e.Index >= 0 && e.Index < _rows.Count)
        {
            var row = _rows[e.Index];
            _rowsBuilt[e.Container] = row;
            if (_showingThumbnails)
            {
                ShowThumbnails(row);
            }
        }
    }

    private void ForgetRowContainer(Control container)
    {
        if (_rowsBuilt.Remove(container, out var row) && _showingThumbnails)
        {
            HideThumbnails(row);
        }
    }

    private static void ShowThumbnails(HistoryRow row)
    {
        foreach (var item in row.Items)
        {
            item.ShowThumbnail();
        }
    }

    private static void HideThumbnails(HistoryRow row)
    {
        foreach (var item in row.Items)
        {
            item.HideThumbnail();
        }
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
