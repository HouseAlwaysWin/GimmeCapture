using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Windows;

public class WindowDetectionService : IWindowDetectionService
{
    private const double CandidateHysteresisPadding = 6.0;
    private const double ChildCandidateMinimumSize = 24.0;
    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_CAPTION = 0x00C00000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect ToAvaloniaRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    public IReadOnlyList<WindowCandidate> GetVisibleWindowCandidates(IntPtr? excludeHWnd = null)
    {
        var candidates = new List<WindowCandidate>();
        var coveredRegions = new List<Rect>();
        IntPtr shellWindow = GetShellWindow();
        IntPtr desktopWindow = GetDesktopWindow();
        int nextZOrder = 0;

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == excludeHWnd || hWnd == shellWindow || hWnd == desktopWindow)
            {
                return true;
            }

            if (!TryBuildTopLevelCandidate(hWnd, nextZOrder, coveredRegions, out var topLevelCandidate))
            {
                return true;
            }

            var resolvedTopLevelCandidate = topLevelCandidate!;
            candidates.Add(resolvedTopLevelCandidate);
            coveredRegions.Add(resolvedTopLevelCandidate.Bounds);
            nextZOrder++;
            AddChildCandidates(resolvedTopLevelCandidate, candidates, ref nextZOrder);
            return true;
        }, IntPtr.Zero);

        return candidates;
    }

    public WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var containingCandidates = candidates.AsValueEnumerable()
            .Where(candidate => candidate.Bounds.Contains(point))
            .OrderBy(candidate => candidate.IsChild ? 0 : 1)
            .ThenBy(candidate => candidate.Area)
            .ThenBy(candidate => candidate.ZOrder)
            .ToList();

        if (containingCandidates.Count == 0)
        {
            if (previousCandidate != null && previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point))
            {
                return previousCandidate;
            }

            return null;
        }

        WindowCandidate bestCandidate = containingCandidates[0];
        if (previousCandidate == null)
        {
            return bestCandidate;
        }

        var matchingPrevious = containingCandidates.AsValueEnumerable()
            .FirstOrDefault(candidate => candidate.IsSameIdentity(previousCandidate));

        if (matchingPrevious != null)
        {
            return ShouldHoldPreviousCandidate(point, matchingPrevious, bestCandidate)
                ? matchingPrevious
                : bestCandidate;
        }

        if (previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point) &&
            IsSameTree(previousCandidate, bestCandidate) &&
            !IsPointSafelyInside(point, bestCandidate.Bounds, CandidateHysteresisPadding))
        {
            return previousCandidate;
        }

        return bestCandidate;
    }

    private static bool ShouldHoldPreviousCandidate(Point point, WindowCandidate previousCandidate, WindowCandidate bestCandidate)
    {
        if (previousCandidate.IsSameIdentity(bestCandidate))
        {
            return true;
        }

        if (!IsSameTree(previousCandidate, bestCandidate))
        {
            return false;
        }

        if (previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point) &&
            !IsPointSafelyInside(point, bestCandidate.Bounds, CandidateHysteresisPadding))
        {
            return true;
        }

        return false;
    }

    private static bool IsSameTree(WindowCandidate left, WindowCandidate right)
    {
        return left.RootHwnd == right.RootHwnd;
    }

    private static bool IsPointSafelyInside(Point point, Rect bounds, double padding)
    {
        if (bounds.Width <= padding * 2 || bounds.Height <= padding * 2)
        {
            return bounds.Contains(point);
        }

        Rect insetBounds = new(
            bounds.X + padding,
            bounds.Y + padding,
            bounds.Width - (padding * 2),
            bounds.Height - (padding * 2));

        return insetBounds.Contains(point);
    }

    private void AddChildCandidates(WindowCandidate parentCandidate, List<WindowCandidate> candidates, ref int nextZOrder)
    {
        int childZOrder = nextZOrder;
        EnumChildWindows(parentCandidate.Hwnd, (childHwnd, _) =>
        {
            if (!TryBuildChildCandidate(parentCandidate, childHwnd, childZOrder, out var childCandidate))
            {
                return true;
            }

            candidates.Add(childCandidate!);
            childZOrder++;
            return true;
        }, IntPtr.Zero);
        nextZOrder = childZOrder;
    }

    private bool TryBuildTopLevelCandidate(IntPtr hWnd, int zOrder, List<Rect> coveredRegions, out WindowCandidate? candidate)
    {
        candidate = null;

        if (!IsWindowVisible(hWnd))
        {
            return false;
        }

        if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (IsWindowCloaked(hWnd))
        {
            return false;
        }

        string title = GetWindowTitle(hWnd);
        string className = GetWindowClassName(hWnd);
        if (string.IsNullOrWhiteSpace(title) && className != "ApplicationFrameWindow")
        {
            return false;
        }

        int style = GetWindowLong(hWnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool isAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!hasCaption && !isAppWindow)
        {
            return false;
        }

        Rect finalRect = GetWindowBounds(hWnd);
        if (finalRect.Width <= 20 || finalRect.Height <= 20)
        {
            return false;
        }

        if (IsRectCovered(finalRect, coveredRegions))
        {
            return false;
        }

        candidate = new WindowCandidate(finalRect, hWnd, IntPtr.Zero, hWnd, zOrder, WindowCandidateKind.TopLevel);
        return true;
    }

    private bool TryBuildChildCandidate(WindowCandidate parentCandidate, IntPtr childHwnd, int zOrder, out WindowCandidate? candidate)
    {
        candidate = null;

        if (!IsWindowVisible(childHwnd))
        {
            return false;
        }

        int exStyle = GetWindowLong(childHwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        Rect childRect = winRectNoDwm(childHwnd).Intersect(parentCandidate.Bounds);
        if (childRect.Width < ChildCandidateMinimumSize || childRect.Height < ChildCandidateMinimumSize)
        {
            return false;
        }

        if (childRect.Width * childRect.Height >= parentCandidate.Area * 0.95)
        {
            return false;
        }

        candidate = new WindowCandidate(
            childRect,
            childHwnd,
            GetParent(childHwnd),
            parentCandidate.RootHwnd,
            zOrder,
            WindowCandidateKind.ChildControl);

        return true;
    }

    private Rect GetWindowBounds(IntPtr hWnd)
    {
        RECT dwmRect;
        int result = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out dwmRect, Marshal.SizeOf(typeof(RECT)));
        return result == 0 ? dwmRect.ToAvaloniaRect() : winRectNoDwm(hWnd);
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        int cloaked = 0;
        DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
        return cloaked != 0;
    }

    private Rect winRectNoDwm(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out RECT winRect);
        return winRect.ToAvaloniaRect();
    }

    private static bool IsRectCovered(Rect target, List<Rect> occluders)
    {
        foreach (var occluder in occluders)
        {
            if (occluder.Contains(target))
            {
                return true;
            }

            var intersect = target.Intersect(occluder);
            if (intersect.Width * intersect.Height > (target.Width * target.Height * 0.95))
            {
                return true;
            }
        }

        return false;
    }
}
