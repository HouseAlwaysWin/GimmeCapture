using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Abstractions;

public interface IWindowDetectionService
{
    IReadOnlyList<WindowCandidate> GetVisibleWindowCandidates(IntPtr? excludeHWnd = null);
    WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null);

    /// <summary>
    /// Lists visible top-level windows (title + physical bounds) that can be picked as a recording
    /// target. Excludes the given window (typically the capture overlay) and untitled/empty windows.
    /// </summary>
    IReadOnlyList<RecordableWindow> GetRecordableWindows(IntPtr? excludeHWnd = null);
}
