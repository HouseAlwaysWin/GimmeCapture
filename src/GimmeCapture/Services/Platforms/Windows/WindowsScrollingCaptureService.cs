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
    private const int WheelNotchesPerStep = 3;     // amount scrolled each step (must be < a viewport so frames overlap)
    private const int RenderDelayMs = 250;         // wait for content to render after scrolling
    private const int MaxFrames = 60;              // hard cap on scroll steps
    private const int MaxHeightMultiplier = 40;    // result height cap = viewport height * this
    private const int MinNewRowsToContinue = 2;    // below this we consider the bottom reached
    private const int ScrollbarIgnorePx = 18;      // ignore a moving scrollbar on the right edge

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

        FocusWindowUnderPoint(centerX, centerY);
        SetCursorPos(centerX, centerY);
        await Task.Delay(120, cancellationToken).ConfigureAwait(false);

        SKBitmap accumulated = await _captureService
            .CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor: false)
            .ConfigureAwait(false);
        SKBitmap previousFrame = accumulated.Copy();

        int ignoreRight = (int)(ScrollbarIgnorePx * visualScaling);
        int minOverlap = Math.Max(8, physH / 10);
        int maxHeight = physH * MaxHeightMultiplier;

        try
        {
            for (int i = 0; i < MaxFrames; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SendWheel(centerX, centerY, -WheelNotchesPerStep);
                await Task.Delay(RenderDelayMs, cancellationToken).ConfigureAwait(false);
                SetCursorPos(centerX, centerY);

                SKBitmap next = await _captureService
                    .CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor: false)
                    .ConfigureAwait(false);

                int overlap = ScrollStitcher.FindVerticalOverlap(previousFrame, next, minOverlap, ignoreRight);
                int newRows = next.Height - overlap;

                // overlap == 0: no common rows found (e.g. scrolled more than a viewport) — stop
                // rather than risk duplicating/garbling content.
                // newRows below threshold: the view did not move, i.e. we reached the bottom.
                if (overlap == 0 || newRows < MinNewRowsToContinue)
                {
                    next.Dispose();
                    break;
                }

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

    private void FocusWindowUnderPoint(int screenX, int screenY)
    {
        try
        {
            var candidates = _windowDetectionService.GetVisibleWindowCandidates();
            var target = _windowDetectionService.GetCandidateAtPoint(new Point(screenX, screenY), candidates);
            if (target != null)
            {
                IntPtr hwnd = target.RootHwnd != IntPtr.Zero ? target.RootHwnd : target.Hwnd;
                if (hwnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hwnd);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ScrollingCapture.FocusWindow", ex);
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
}
