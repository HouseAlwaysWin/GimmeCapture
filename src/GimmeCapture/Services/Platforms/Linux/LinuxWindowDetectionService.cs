using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// Placeholder <see cref="IWindowDetectionService"/> for non-Windows (Linux) runs. Enumerating and
/// hit-testing top-level windows needs X11/Wayland queries — future work
/// (docs/LINUX_PORT_FEASIBILITY.md). Returns empty sets so window-pick features degrade gracefully.
/// </summary>
public sealed class LinuxWindowDetectionService : IWindowDetectionService
{
    private static readonly IReadOnlyList<WindowCandidate> NoCandidates = Array.Empty<WindowCandidate>();
    private static readonly IReadOnlyList<RecordableWindow> NoWindows = Array.Empty<RecordableWindow>();

    public IReadOnlyList<WindowCandidate> GetVisibleWindowCandidates(IntPtr? excludeHWnd = null) => NoCandidates;

    public WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null) => null;

    public IReadOnlyList<RecordableWindow> GetRecordableWindows(IntPtr? excludeHWnd = null) => NoWindows;
}
