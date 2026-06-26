using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    // The overlay's own window handle, captured in RefreshWindowRects, so it is excluded from the
    // window picker (otherwise the transparent full-screen capture overlay would appear in the list).
    private IntPtr? _selfWindowHandle;

    // HWNDs of the windows picked for recording. 1 = single-window WGC (follows the window); 2+ = multi
    // window (composite/separate per RecordMultiWindowMode). Empty = region/monitor (gdigrab). Cleared by
    // the SelectionRect setter whenever the selection changes to anything else (redraw / monitor / region).
    private readonly List<IntPtr> _recordWindowHandles = new();

    // Set true while SelectCaptureTarget assigns the bounding-union rect, so the SelectionRect setter
    // does not wipe the multi-selection it is building.
    private bool _suppressRecordHandleClear;

    private bool _recordSeparateFiles;

    /// <summary>HWNDs of the picked recording-target windows (empty for region/monitor capture).</summary>
    internal IReadOnlyList<IntPtr> RecordWindowHandles => _recordWindowHandles;

    /// <summary>When multiple windows are picked, whether to record each to its own file (vs. one composite video).</summary>
    public bool RecordSeparateFiles
    {
        get => _recordSeparateFiles;
        set => this.RaiseAndSetIfChanged(ref _recordSeparateFiles, value);
    }

    /// <summary>Multi-window output mode derived from <see cref="RecordSeparateFiles"/>.</summary>
    internal MultiWindowMode RecordMultiWindowMode =>
        _recordSeparateFiles ? MultiWindowMode.Separate : MultiWindowMode.Composite;

    /// <summary>
    /// Targets shown in the record-mode capture-scope picker: each monitor and each visible top-level
    /// window. Picking a monitor sets the recording selection to its bounds (gdigrab path). Picking one or
    /// more windows records them via Windows Graphics Capture (follows the windows).
    /// </summary>
    public ObservableCollection<CaptureTargetItem> CaptureTargets { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshCaptureTargetsCommand { get; set; } = null!;
    public ReactiveCommand<CaptureTargetItem, Unit> SelectCaptureTargetCommand { get; set; } = null!;

    private void InitializeCaptureScopeCommands()
    {
        RefreshCaptureTargetsCommand = ReactiveCommand.Create(RefreshCaptureTargets);
        RefreshCaptureTargetsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"RefreshCaptureTargets error: {ex}"));

        SelectCaptureTargetCommand = ReactiveCommand.Create<CaptureTargetItem>(SelectCaptureTarget);
        SelectCaptureTargetCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"SelectCaptureTarget error: {ex}"));
    }

    /// <summary>Rebuilds the monitor + window list. Called when the picker flyout opens.</summary>
    public void RefreshCaptureTargets()
    {
        CaptureTargets.Clear();

        // Monitors: AllScreenBounds is already in the overlay's logical coordinate space.
        if (AllScreenBounds is { Count: > 0 })
        {
            for (int i = 0; i < AllScreenBounds.Count; i++)
            {
                var s = AllScreenBounds[i];
                if (s.W <= 0 || s.H <= 0)
                {
                    continue;
                }

                string name = string.Format(
                    LocalizationService.Instance["CaptureScopeMonitor"] ?? "Monitor {0}", i + 1);
                CaptureTargets.Add(new CaptureTargetItem(name, new Rect(s.X, s.Y, s.W, s.H), isMonitor: true));
            }
        }

        // Windows: physical bounds -> logical (reverse of the gdigrab transform x*scaling + offset).
        foreach (var win in _detectionService.GetRecordableWindows(_selfWindowHandle))
        {
            var logical = CaptureScopeGeometry.PhysicalToLogical(win.Bounds, ScreenOffset, VisualScaling);
            if (logical.Width <= 0 || logical.Height <= 0)
            {
                continue;
            }

            // Re-mark windows that are still in the current multi-selection (so reopening the flyout keeps state).
            bool selected = _recordWindowHandles.Contains(win.Hwnd);
            CaptureTargets.Add(new CaptureTargetItem(win.Title, logical, isMonitor: false, win.Hwnd) { IsSelected = selected });
        }
    }

    private void SelectCaptureTarget(CaptureTargetItem? target)
    {
        if (target == null || CurrentMode == SnipMode.Translation || RecState != RecordingState.Idle)
        {
            return;
        }

        DeactivateDrawingInteraction();

        if (target.IsMonitor || target.Hwnd == IntPtr.Zero)
        {
            // Monitor / region: single-select. The setter clears the window multi-selection.
            SelectionRect = target.LogicalBounds;
            CurrentState = SnipState.Selected;
            return;
        }

        // Window: toggle it in/out of the multi-selection and recompute the bounding union.
        target.IsSelected = !target.IsSelected;
        RebuildRecordWindowSelection();

        if (_recordWindowHandles.Count > 0)
        {
            CurrentState = SnipState.Selected;
        }
    }

    /// <summary>Rebuilds <see cref="_recordWindowHandles"/> from the selected window items and sets the selection to their union.</summary>
    private void RebuildRecordWindowSelection()
    {
        _recordWindowHandles.Clear();
        Rect? union = null;
        foreach (var t in CaptureTargets)
        {
            if (t.IsMonitor || !t.IsSelected || t.Hwnd == IntPtr.Zero)
            {
                continue;
            }

            _recordWindowHandles.Add(t.Hwnd);
            union = union is { } u ? u.Union(t.LogicalBounds) : t.LogicalBounds;
        }

        if (union is { } rect)
        {
            _suppressRecordHandleClear = true;
            SelectionRect = rect;
            _suppressRecordHandleClear = false;
        }
    }

    /// <summary>Drops the picked recording windows (called by the SelectionRect setter on a real selection change).</summary>
    private void ClearRecordWindowSelection()
    {
        _recordWindowHandles.Clear();
        foreach (var t in CaptureTargets)
        {
            if (t.IsSelected)
            {
                t.IsSelected = false;
            }
        }
    }
}

/// <summary>
/// Pure geometry for the capture-scope picker. Converts a window's physical-pixel rectangle into the
/// overlay's logical coordinate space — the exact inverse of the recording transform
/// (<c>logical.X * scaling + offset.X</c>) so the picked rect captures the right area.
/// </summary>
public static class CaptureScopeGeometry
{
    public static Rect PhysicalToLogical(Rect physical, PixelPoint offset, double scaling)
    {
        double s = scaling > 0 ? scaling : 1.0;
        return new Rect(
            (physical.X - offset.X) / s,
            (physical.Y - offset.Y) / s,
            physical.Width / s,
            physical.Height / s);
    }
}

/// <summary>One entry in the capture-scope picker: a monitor or a window, with its logical bounds.</summary>
public sealed class CaptureTargetItem : ReactiveObject
{
    private bool _isSelected;

    public CaptureTargetItem(string displayName, Rect logicalBounds, bool isMonitor, IntPtr hwnd = default)
    {
        DisplayName = displayName;
        LogicalBounds = logicalBounds;
        IsMonitor = isMonitor;
        Hwnd = hwnd;
    }

    public string DisplayName { get; }
    public Rect LogicalBounds { get; }
    public bool IsMonitor { get; }

    /// <summary>The window handle for window targets (used by WGC capture); <see cref="IntPtr.Zero"/> for monitors.</summary>
    public IntPtr Hwnd { get; }

    /// <summary>Whether this window is part of the current multi-window selection (shown as a check in the picker).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}
