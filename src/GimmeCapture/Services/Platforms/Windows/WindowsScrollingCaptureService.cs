using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;

namespace GimmeCapture.Services.Platforms.Windows;

/// <summary>
/// Windows implementation of scrolling capture: focuses the window under the region,
/// sends mouse-wheel scrolls, captures each frame via <see cref="IScreenCaptureService"/>,
/// and stitches them with <see cref="ScrollStitcher"/>.
/// </summary>
public sealed class WindowsScrollingCaptureService : IScrollingCaptureService
{
    // Tunables for the scroll loop.
    private const int InitialSettleMs = 350;       // wait for the target to focus + paint before the first frame
    private const int RenderDelayMs = 350;         // wait for content to render after scrolling (smooth-scroll settle)
    private const int MaxFrames = 150;             // hard cap on total scroll iterations (incl. retries)
    private const int MaxHeightMultiplier = 40;    // result height cap = viewport height * this
    private const int MinNewRowsToContinue = 2;    // below this an attempt counts as "no progress"
    private const int MaxNoProgressAttempts = 3;   // consecutive no-progress attempts before concluding bottom
    private const int ScrollbarIgnorePx = 18;      // ignore a moving scrollbar on the right edge
    private const double RowMismatchTolerance = 0.12; // fraction of overlap rows allowed to differ

    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint INPUT_MOUSE = 0;
    private const int WHEEL_DELTA = 120;

    private readonly IScreenCaptureService _captureService;
    private readonly IWindowDetectionService _windowDetectionService;

    public WindowsScrollingCaptureService(
        IScreenCaptureService captureService,
        IWindowDetectionService windowDetectionService)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _windowDetectionService = windowDetectionService ?? throw new ArgumentNullException(nameof(windowDetectionService));
    }

    public async Task<SKBitmap?> CaptureAsync(
        Rect region,
        PixelPoint screenOffset,
        double visualScaling,
        CancellationToken cancellationToken = default)
    {
        int physX = (int)(region.X * visualScaling) + screenOffset.X;
        int physY = (int)(region.Y * visualScaling) + screenOffset.Y;
        int physW = (int)(region.Width * visualScaling);
        int physH = (int)(region.Height * visualScaling);
        if (physW <= 0 || physH <= 0)
        {
            return null;
        }

        int centerX = physX + (physW / 2);
        int centerY = physY + (physH / 2);

        IntPtr targetHwnd = ResolveTargetHwnd(centerX, centerY);
        ForceForeground(targetHwnd);
        SetCursorPos(centerX, centerY);
        await Task.Delay(InitialSettleMs, cancellationToken).ConfigureAwait(false);

        SKBitmap accumulated = await _captureService
            .CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor: false)
            .ConfigureAwait(false);
        SKBitmap previousFrame = accumulated.Copy();

        int ignoreRight = (int)(ScrollbarIgnorePx * visualScaling);
        int minOverlap = Math.Max(8, physH / 10);
        int maxHeight = physH * MaxHeightMultiplier;
        int noProgress = 0;

        // Scroll only a fraction of the region per step so consecutive frames always overlap
        // (a fixed large step over-scrolls small regions, leaving no common rows to stitch).
        // ~1 notch per ~400px of region height, capped, so small regions step gently.
        int wheelNotches = Math.Clamp(physH / 400, 1, 3);

        try
        {
            for (int i = 0; i < MaxFrames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Re-assert focus + cursor each step so the wheel reaches the target window
                // (important when triggered via a global hotkey, where focus can drift).
                ForceForeground(targetHwnd);
                SetCursorPos(centerX, centerY);
                SendWheel(centerX, centerY, -wheelNotches);
                await Task.Delay(RenderDelayMs, cancellationToken).ConfigureAwait(false);

                SKBitmap next = await _captureService
                    .CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor: false)
                    .ConfigureAwait(false);

                int overlap = ScrollStitcher.FindVerticalOverlap(previousFrame, next, minOverlap, ignoreRight, RowMismatchTolerance);
                int newRows = overlap == 0 ? 0 : next.Height - overlap;

                // No new content this attempt (didn't scroll yet / couldn't align). Retry a few
                // times before concluding we've reached the bottom — this absorbs transient focus
                // and render lag instead of stopping after a single frame.
                if (newRows < MinNewRowsToContinue)
                {
                    next.Dispose();
                    if (++noProgress >= MaxNoProgressAttempts)
                    {
                        break;
                    }

                    continue;
                }

                noProgress = 0;
                SKBitmap grown = ScrollStitcher.Append(accumulated, next, overlap);
                accumulated.Dispose();
                accumulated = grown;

                previousFrame.Dispose();
                previousFrame = next; // next becomes the new "previous frame" (owned here)

                if (accumulated.Height >= maxHeight)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Return whatever was stitched so far.
        }
        catch (Exception ex)
        {
            AppLog.Error("ScrollingCapture.CaptureAsync", ex);
        }
        finally
        {
            previousFrame.Dispose();
        }

        return accumulated;
    }

    private IntPtr ResolveTargetHwnd(int screenX, int screenY)
    {
        try
        {
            var candidates = _windowDetectionService.GetVisibleWindowCandidates();
            var target = _windowDetectionService.GetCandidateAtPoint(new Point(screenX, screenY), candidates);
            if (target != null)
            {
                return target.RootHwnd != IntPtr.Zero ? target.RootHwnd : target.Hwnd;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ScrollingCapture.FocusWindow", ex);
        }

        return IntPtr.Zero;
    }

    // SetForegroundWindow is unreliable across processes (especially from a global-hotkey
    // context). Attaching our input thread to the current foreground thread lets it succeed.
    private static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint appThread = GetCurrentThreadId();
            if (foreThread != appThread)
            {
                AttachThreadInput(foreThread, appThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(foreThread, appThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ScrollingCapture.ForceForeground", ex);
        }
    }

    private static void SendWheel(int screenX, int screenY, int notches)
    {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].U.mi = new MOUSEINPUT
        {
            dx = screenX,
            dy = screenY,
            mouseData = unchecked((uint)(notches * WHEEL_DELTA)),
            dwFlags = MOUSEEVENTF_WHEEL,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
