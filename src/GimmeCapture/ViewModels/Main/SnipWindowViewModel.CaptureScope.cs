using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using Avalonia;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    // The overlay's own window handle, captured in RefreshWindowRects, so it is excluded from the
    // window picker (otherwise the transparent full-screen capture overlay would appear in the list).
    private IntPtr? _selfWindowHandle;

    // Step 2 WGC probe: null in design/test contexts and on unsupported OSes.
    private readonly IWgcWindowCaptureProbe? _wgcProbe;

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

            CaptureTargets.Add(new CaptureTargetItem(win.Title, logical, isMonitor: false, win.Hwnd));
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

        // Step 2 WGC probe (temporary): when a real window is picked, grab one frame via Windows Graphics
        // Capture and save it as a PNG to confirm the interop works (not all-black) before Step 3 wires
        // WGC into the encoder. Fire-and-forget; never blocks or breaks the picker.
        if (!target.IsMonitor && target.Hwnd != IntPtr.Zero)
        {
            TriggerWgcProbe(target.Hwnd, target.DisplayName);
        }
    }

    private void TriggerWgcProbe(IntPtr hwnd, string title)
    {
        var probe = _wgcProbe;
        if (probe == null)
        {
            return;
        }

        string baseDir = _mainVm?.AppSettingsService?.BaseDataDirectory ?? AppContext.BaseDirectory;
        string outputPath = Path.Combine(baseDir, "wgc-probe.png");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            bool ok;
            try
            {
                ok = await probe.CaptureWindowToPngAsync(hwnd, outputPath);
            }
            catch (Exception ex)
            {
                AppLog.Error("SnipRecording.WgcProbe", ex);
                ok = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                string message = ok
                    ? $"WGC probe saved: {outputPath}"
                    : $"WGC probe failed for \"{title}\" — see log.";
                AppLog.Information($"SnipRecording.WgcProbe: {message}");
                _mainVm?.ShowToastAction?.Invoke(
                    message,
                    ok ? MainWindowViewModel.ToastSeverity.Success : MainWindowViewModel.ToastSeverity.Error);
            });
        });
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
}
