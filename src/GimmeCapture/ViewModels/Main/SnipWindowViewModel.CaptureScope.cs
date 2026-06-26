using System;
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

    // When the user picks a window target, remember its title + the selection rect we set for it. The
    // title is only honored at record start while SelectionRect still matches — so redrawing a region or
    // picking a monitor/fullscreen automatically reverts to region capture without extra bookkeeping.
    private string? _captureWindowTitle;
    private Rect _captureWindowRect;

    /// <summary>
    /// The window title to record via gdigrab title=, or "" for region/monitor capture. Returned only
    /// while the current selection still matches the window the user picked.
    /// </summary>
    public string GetActiveCaptureWindowTitle()
    {
        if (string.IsNullOrEmpty(_captureWindowTitle))
        {
            return string.Empty;
        }

        var r = SelectionRect;
        bool stillOnWindow =
            Math.Abs(r.X - _captureWindowRect.X) < 1.0 &&
            Math.Abs(r.Y - _captureWindowRect.Y) < 1.0 &&
            Math.Abs(r.Width - _captureWindowRect.Width) < 1.0 &&
            Math.Abs(r.Height - _captureWindowRect.Height) < 1.0;

        return stillOnWindow ? _captureWindowTitle! : string.Empty;
    }

    /// <summary>
    /// Targets shown in the record-mode capture-scope picker: each monitor and each visible top-level
    /// window. Picking one sets the recording selection to its bounds — the same path fullscreen-select
    /// uses — so capture flows through the existing gdigrab offset+size path unchanged.
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

            CaptureTargets.Add(new CaptureTargetItem(win.Title, logical, isMonitor: false, windowTitle: win.Title));
        }
    }

    private void SelectCaptureTarget(CaptureTargetItem? target)
    {
        if (target == null || CurrentMode == SnipMode.Translation || RecState != RecordingState.Idle)
        {
            return;
        }

        // Mirror SelectFullscreenCommand: drop any in-progress drawing, set the selection to the target
        // bounds, and move to the Selected state so the record toolbar appears over it.
        DeactivateDrawingInteraction();
        SelectionRect = target.LogicalBounds;
        CurrentState = SnipState.Selected;

        // Window targets record via gdigrab title= (follows the window); monitors record their region.
        if (target.WindowTitle is { Length: > 0 })
        {
            _captureWindowTitle = target.WindowTitle;
            _captureWindowRect = target.LogicalBounds;
        }
        else
        {
            _captureWindowTitle = null;
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
public sealed class CaptureTargetItem
{
    public CaptureTargetItem(string displayName, Rect logicalBounds, bool isMonitor, string? windowTitle = null)
    {
        DisplayName = displayName;
        LogicalBounds = logicalBounds;
        IsMonitor = isMonitor;
        WindowTitle = windowTitle;
    }

    public string DisplayName { get; }
    public Rect LogicalBounds { get; }
    public bool IsMonitor { get; }

    /// <summary>Exact window title for gdigrab title= capture, or null for a monitor target.</summary>
    public string? WindowTitle { get; }
}
