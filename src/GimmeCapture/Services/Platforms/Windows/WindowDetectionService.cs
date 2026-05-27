using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using Microsoft.Win32;

namespace GimmeCapture.Services.Platforms.Windows;

public class WindowDetectionService : IWindowDetectionService
{
    private const double CandidateHysteresisPadding = 6.0;
    private const double ChildCandidateMinimumSize = 24.0;
    private const int SyntheticTaskbarHandleBase = unchecked((int)0x70000000);
    private const int SyntheticTaskbarChildHandleBase = unchecked((int)0x71000000);
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

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

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

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
            if (ShouldSkipEnumeratedTopLevelWindow(hWnd, excludeHWnd, shellWindow, desktopWindow))
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

        AddMonitorWorkAreaStripCandidates(candidates, ref nextZOrder);

        return candidates;
    }

    public static Rect? TryGetDockedStripBounds(Rect monitorBounds, Rect workArea)
    {
        const double tolerance = 0.5;
        if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
        {
            return null;
        }

        if (Math.Abs(monitorBounds.Left - workArea.Left) <= tolerance &&
            Math.Abs(monitorBounds.Right - workArea.Right) <= tolerance)
        {
            if (workArea.Top > monitorBounds.Top + tolerance)
            {
                return new Rect(
                    monitorBounds.Left,
                    monitorBounds.Top,
                    monitorBounds.Width,
                    workArea.Top - monitorBounds.Top);
            }

            if (workArea.Bottom < monitorBounds.Bottom - tolerance)
            {
                return new Rect(
                    monitorBounds.Left,
                    workArea.Bottom,
                    monitorBounds.Width,
                    monitorBounds.Bottom - workArea.Bottom);
            }
        }

        if (Math.Abs(monitorBounds.Top - workArea.Top) <= tolerance &&
            Math.Abs(monitorBounds.Bottom - workArea.Bottom) <= tolerance)
        {
            if (workArea.Left > monitorBounds.Left + tolerance)
            {
                return new Rect(
                    monitorBounds.Left,
                    monitorBounds.Top,
                    workArea.Left - monitorBounds.Left,
                    monitorBounds.Height);
            }

            if (workArea.Right < monitorBounds.Right - tolerance)
            {
                return new Rect(
                    workArea.Right,
                    monitorBounds.Top,
                    monitorBounds.Right - workArea.Right,
                    monitorBounds.Height);
            }
        }

        return null;
    }

    internal static IReadOnlyList<Rect> BuildSyntheticTaskbarSegments(Rect stripBounds, bool centeredAlignment)
    {
        var segments = new List<Rect>(3);
        if (stripBounds.Width < ChildCandidateMinimumSize || stripBounds.Height < ChildCandidateMinimumSize)
        {
            return segments;
        }

        bool isHorizontal = stripBounds.Width >= stripBounds.Height;
        if (isHorizontal)
        {
            double trayWidth = ClampDimension(stripBounds.Width * 0.16, 220, 380, stripBounds.Width * 0.45);
            Rect trayRect = new(
                stripBounds.Right - trayWidth,
                stripBounds.Top,
                trayWidth,
                stripBounds.Height);

            double usableWidth = Math.Max(0, stripBounds.Width - trayWidth - 24);
            if (usableWidth < ChildCandidateMinimumSize)
            {
                segments.Add(trayRect);
                return segments;
            }

            Rect commandRect;
            Rect appBandRect;
            if (centeredAlignment)
            {
                double centeredBandWidth = ClampDimension(stripBounds.Width * 0.42, 320, 920, usableWidth);
                double centeredBandX = stripBounds.Left + Math.Max(0, ((stripBounds.Width - trayWidth) - centeredBandWidth) / 2);
                double commandWidth = ClampDimension(centeredBandWidth * 0.38, 180, 320, centeredBandWidth * 0.65);
                commandRect = new(centeredBandX, stripBounds.Top, commandWidth, stripBounds.Height);
                appBandRect = new(
                    commandRect.Right,
                    stripBounds.Top,
                    Math.Max(0, centeredBandWidth - commandWidth),
                    stripBounds.Height);
            }
            else
            {
                double leftBandWidth = ClampDimension(stripBounds.Width * 0.48, 360, 920, usableWidth);
                double commandWidth = ClampDimension(leftBandWidth * 0.30, 160, 320, leftBandWidth * 0.6);
                commandRect = new(stripBounds.Left, stripBounds.Top, commandWidth, stripBounds.Height);
                appBandRect = new(
                    commandRect.Right,
                    stripBounds.Top,
                    Math.Max(0, leftBandWidth - commandWidth),
                    stripBounds.Height);
            }

            AddIfLargeEnough(segments, commandRect);
            AddIfLargeEnough(segments, appBandRect);
            AddIfLargeEnough(segments, trayRect);
            return segments;
        }

        double trayHeight = ClampDimension(stripBounds.Height * 0.18, 160, 320, stripBounds.Height * 0.45);
        Rect verticalTrayRect = new(
            stripBounds.Left,
            stripBounds.Bottom - trayHeight,
            stripBounds.Width,
            trayHeight);

        double usableHeight = Math.Max(0, stripBounds.Height - trayHeight - 24);
        if (usableHeight < ChildCandidateMinimumSize)
        {
            segments.Add(verticalTrayRect);
            return segments;
        }

        double topBandHeight = ClampDimension(stripBounds.Height * 0.48, 280, 920, usableHeight);
        double commandHeight = ClampDimension(topBandHeight * 0.22, 120, 240, topBandHeight * 0.5);
        Rect verticalCommandRect = new(stripBounds.Left, stripBounds.Top, stripBounds.Width, commandHeight);
        Rect verticalAppsRect = new(
            stripBounds.Left,
            verticalCommandRect.Bottom,
            stripBounds.Width,
            Math.Max(0, topBandHeight - commandHeight));

        AddIfLargeEnough(segments, verticalCommandRect);
        AddIfLargeEnough(segments, verticalAppsRect);
        AddIfLargeEnough(segments, verticalTrayRect);
        return segments;
    }

    private static bool ShouldSkipEnumeratedTopLevelWindow(
        IntPtr hWnd,
        IntPtr? excludeHWnd,
        IntPtr shellWindow,
        IntPtr desktopWindow)
    {
        if (hWnd == excludeHWnd || hWnd == desktopWindow)
        {
            return true;
        }

        if (hWnd != shellWindow)
        {
            return false;
        }

        string className = GetWindowClassName(hWnd);
        return !IsTaskbarWindow(className);
    }

    public WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        WindowCandidate? bestCandidate = null;
        WindowCandidate? previousMatch = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Bounds.Contains(point))
            {
                continue;
            }

            if (previousCandidate != null && candidate.IsSameIdentity(previousCandidate))
            {
                previousMatch = candidate;
            }

            if (bestCandidate == null || CompareCandidates(candidate, bestCandidate) < 0)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null)
        {
            if (previousCandidate != null && previousCandidate.Bounds.Inflate(CandidateHysteresisPadding).Contains(point))
            {
                return previousCandidate;
            }

            return null;
        }

        if (previousCandidate == null)
        {
            return bestCandidate;
        }

        if (previousMatch != null)
        {
            return ShouldHoldPreviousCandidate(point, previousMatch, bestCandidate)
                ? previousMatch
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

    private static int CompareCandidates(WindowCandidate left, WindowCandidate right)
    {
        int childPriority = (left.IsChild ? 0 : 1).CompareTo(right.IsChild ? 0 : 1);
        if (childPriority != 0)
        {
            return childPriority;
        }

        int areaPriority = left.Area.CompareTo(right.Area);
        if (areaPriority != 0)
        {
            return areaPriority;
        }

        return left.ZOrder.CompareTo(right.ZOrder);
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

    private void AddMonitorWorkAreaStripCandidates(List<WindowCandidate> candidates, ref int nextZOrder)
    {
        int monitorIndex = 0;
        int stripZOrder = nextZOrder;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT monitorRect, IntPtr dwData) =>
        {
            var monitorInfo = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty
            };

            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                monitorIndex++;
                return true;
            }

            Rect monitorBounds = monitorInfo.rcMonitor.ToAvaloniaRect();
            Rect workArea = monitorInfo.rcWork.ToAvaloniaRect();
            Rect? stripBounds = TryGetDockedStripBounds(monitorBounds, workArea);
            if (stripBounds is not { } strip || strip.Width < ChildCandidateMinimumSize || strip.Height < ChildCandidateMinimumSize)
            {
                monitorIndex++;
                return true;
            }

            if (!IsCandidateRectAlreadyRepresented(strip, candidates))
            {
                IntPtr syntheticHandle = new(SyntheticTaskbarHandleBase + monitorIndex);
                var syntheticStripCandidate = new WindowCandidate(
                    strip,
                    syntheticHandle,
                    IntPtr.Zero,
                    syntheticHandle,
                    stripZOrder++,
                    WindowCandidateKind.TopLevel);

                candidates.Add(syntheticStripCandidate);
                AddSyntheticTaskbarChildCandidates(syntheticStripCandidate, candidates, ref stripZOrder);
            }

            monitorIndex++;
            return true;
        }, IntPtr.Zero);
        nextZOrder = stripZOrder;
    }

    private void AddSyntheticTaskbarChildCandidates(WindowCandidate stripCandidate, List<WindowCandidate> candidates, ref int nextZOrder)
    {
        bool centeredAlignment = IsTaskbarCenteredAlignment();
        IReadOnlyList<Rect> segments = BuildSyntheticTaskbarSegments(stripCandidate.Bounds, centeredAlignment);
        for (int i = 0; i < segments.Count; i++)
        {
            IntPtr childHandle = new(SyntheticTaskbarChildHandleBase + (stripCandidate.Hwnd.ToInt32() - SyntheticTaskbarHandleBase) * 10 + i);
            candidates.Add(new WindowCandidate(
                segments[i],
                childHandle,
                stripCandidate.Hwnd,
                stripCandidate.RootHwnd,
                nextZOrder++,
                WindowCandidateKind.ChildControl));
        }
    }

    private static bool IsTaskbarCenteredAlignment()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", writable: false);
            object? value = key?.GetValue("TaskbarAl");
            return value switch
            {
                int intValue => intValue == 1,
                byte byteValue => byteValue == 1,
                _ => true
            };
        }
        catch
        {
            return true;
        }
    }

    private static double ClampDimension(double preferred, double min, double max, double available)
    {
        double resolved = Math.Min(preferred, max);
        resolved = Math.Max(resolved, min);
        return Math.Min(resolved, Math.Max(available, min));
    }

    private static void AddIfLargeEnough(List<Rect> segments, Rect rect)
    {
        if (rect.Width >= ChildCandidateMinimumSize && rect.Height >= ChildCandidateMinimumSize)
        {
            segments.Add(rect);
        }
    }

    private static bool IsCandidateRectAlreadyRepresented(Rect target, List<WindowCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Kind != WindowCandidateKind.TopLevel)
            {
                continue;
            }

            var intersect = candidate.Bounds.Intersect(target);
            double intersectionArea = intersect.Width * intersect.Height;
            if (intersectionArea <= 0)
            {
                continue;
            }

            double targetArea = target.Width * target.Height;
            double candidateArea = candidate.Area;
            bool mostlyOverlapsTarget = intersectionArea >= targetArea * 0.95;
            bool areaComparable = candidateArea <= targetArea * 1.15 && candidateArea >= targetArea * 0.85;
            if (mostlyOverlapsTarget && areaComparable)
            {
                return true;
            }
        }

        return false;
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

        string className = GetWindowClassName(hWnd);
        bool isTaskbarWindow = IsTaskbarWindow(className);
        string title = GetWindowTitle(hWnd);
        if (string.IsNullOrWhiteSpace(title) && className != "ApplicationFrameWindow" && !isTaskbarWindow)
        {
            return false;
        }

        int style = GetWindowLong(hWnd, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool isAppWindow = (exStyle & WS_EX_APPWINDOW) != 0;
        if (!hasCaption && !isAppWindow && !isTaskbarWindow)
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

    private static bool IsTaskbarWindow(string className)
    {
        return string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal)
            || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal);
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
