using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

// Manual scrolling capture: after the region is selected the overlay hides, the user
// scrolls the target window by hand, and the app captures the region on a timer and
// stitches new content live. F6 finishes (opens the long pin); Esc cancels.
public partial class SnipWindowViewModel
{
    private DispatcherTimer? _manualScrollTimer;
    private SKBitmap? _manualAccumulated;
    private SKBitmap? _manualPrevFrame;
    private bool _manualScrollActive;
    private int _manualTickRunning;
    private int _manualMaxHeight;
    private int _manualMinOverlap;
    private int _manualIgnoreRight;

    private const int ManualTickMs = 90;            // capture often so fast scrolls still overlap
    private const int ManualMinNewRows = 2;
    private const double ManualRowMismatchTolerance = 0.20; // tolerate a static header/footer in the region

    /// <summary>True while a manual scrolling-capture session is running (overlay hidden).</summary>
    public bool IsManualScrollActive => _manualScrollActive;

    /// <summary>Show the on-screen hint (capture-excluded) with the localized instruction.</summary>
    public Action? ShowScrollingHintAction { get; set; }

    /// <summary>Update the hint with the current captured height (physical px).</summary>
    public Action<int>? UpdateScrollingHintAction { get; set; }

    /// <summary>Hide/close the hint window.</summary>
    public Action? HideScrollingHintAction { get; set; }

    private async Task StartManualScrollCaptureAsync()
    {
        if (_manualScrollActive)
        {
            return;
        }

        await _captureVisibilityCoordinator.HideAndWaitForCaptureAsync(HideAction ?? (() => { }));
        // Give the overlay time to disappear and the target to repaint before the first frame.
        await Task.Delay(150);

        double scaling = VisualScaling <= 0 ? 1.0 : VisualScaling;
        int physH = (int)(SelectionRect.Height * scaling);
        _manualMinOverlap = Math.Max(8, physH / 10);
        _manualIgnoreRight = (int)(18 * scaling);
        _manualMaxHeight = Math.Max(physH, physH * 40);

        try
        {
            _manualAccumulated = await _captureService.CaptureScreenAsync(SelectionRect, ScreenOffset, VisualScaling, false);
        }
        catch (Exception ex)
        {
            AppLog.Error("ManualScroll.FirstFrame", ex);
            CloseAction?.Invoke();
            return;
        }

        _manualPrevFrame = _manualAccumulated.Copy();
        _manualScrollActive = true;

        // Finish (F6) / cancel (Esc) are handled by the snip key hook (SnipWindow.Win32.cs),
        // which receives keys even while the overlay is hidden — no separate global hotkey needed.

        ShowScrollingHintAction?.Invoke();
        UpdateScrollingHintAction?.Invoke(_manualAccumulated.Height);

        _manualScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ManualTickMs) };
        _manualScrollTimer.Tick += (_, _) => ManualScrollTick();
        _manualScrollTimer.Start();
    }

    private async void ManualScrollTick()
    {
        if (!_manualScrollActive)
        {
            return;
        }

        if (Interlocked.Exchange(ref _manualTickRunning, 1) == 1)
        {
            return;
        }

        try
        {
            SKBitmap next;
            try
            {
                next = await _captureService.CaptureScreenAsync(SelectionRect, ScreenOffset, VisualScaling, false);
            }
            catch (Exception ex)
            {
                AppLog.Warning("ManualScroll.Capture", ex);
                return;
            }

            // Finish/cancel may have run during the capture await.
            if (!_manualScrollActive || _manualPrevFrame == null || _manualAccumulated == null)
            {
                next.Dispose();
                return;
            }

            // Stitch synchronously on the UI thread so it can't race FinishManualScrollCapture.
            int overlap = ScrollStitcher.FindVerticalOverlap(
                _manualPrevFrame, next, _manualMinOverlap, _manualIgnoreRight, ManualRowMismatchTolerance);
            int newRows = overlap == 0 ? 0 : next.Height - overlap;

            if (newRows >= ManualMinNewRows)
            {
                SKBitmap grown = ScrollStitcher.Append(_manualAccumulated, next, overlap);
                _manualAccumulated.Dispose();
                _manualAccumulated = grown;
                _manualPrevFrame.Dispose();
                _manualPrevFrame = next;

                UpdateScrollingHintAction?.Invoke(_manualAccumulated.Height);

                if (_manualAccumulated.Height >= _manualMaxHeight)
                {
                    FinishManualScrollCapture(cancelled: false);
                }
            }
            else
            {
                next.Dispose();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _manualTickRunning, 0);
        }
    }

    internal void FinishManualScrollCapture(bool cancelled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => FinishManualScrollCapture(cancelled));
            return;
        }

        if (!_manualScrollActive)
        {
            return;
        }

        _manualScrollActive = false;

        _manualScrollTimer?.Stop();
        _manualScrollTimer = null;

        HideScrollingHintAction?.Invoke();

        SKBitmap? result = _manualAccumulated;
        SKBitmap? prev = _manualPrevFrame;
        _manualAccumulated = null;
        _manualPrevFrame = null;

        try
        {
            if (!cancelled && result != null && result.Width > 0 && result.Height > 0)
            {
                OpenStitchedPin(result);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("ManualScroll.Finish", ex);
        }
        finally
        {
            result?.Dispose();
            prev?.Dispose();
            CloseAction?.Invoke();
        }
    }
}
