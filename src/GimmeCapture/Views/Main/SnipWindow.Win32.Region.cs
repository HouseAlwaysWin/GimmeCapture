using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Input.Raw;

namespace GimmeCapture.Views.Main;

// WndProc hit-testing and window-region computation (translation overlay, screenshot-style rings,
// pass-through). Split out of SnipWindow.Win32.cs (god class reduction) — no behavior change.
// P/Invoke declarations and hit-test fields remain in SnipWindow.Win32.cs (same partial class).
public partial class SnipWindow : Window
{
    public void InitializeWin32Hook()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || _wndProcDelegate != null) return;

        _wndProcDelegate = new WndProcDelegate(WndProcHook);
        IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

        if (IntPtr.Size == 8)
            _oldWndProc = SetWindowLongPtr64(hwnd, GWLP_WNDPROC, ptr);
        else
            _oldWndProc = SetWindowLongPtr32(hwnd, GWLP_WNDPROC, ptr);

        // Also install the LL Keyboard hook for translation global hotkeys
        InstallLLKeyboardHook();
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        bool allowHitTestTransparent = (_viewModel?.IsTranslationMode ?? false) || !(_viewModel?.IsDrawingMode ?? false);
        if (msg == WM_NCHITTEST && _useHitTestRegions && allowHitTestTransparent)
        {
            // Signed screen coords (multi-monitor). Must match SetWindowRgn rects (physical client pixels).
            int lp = lParam.ToInt32();
            var pt = new POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
            ScreenToClient(hWnd, ref pt);
            var point = new Point(pt.X, pt.Y);

            bool hit = false;
            foreach (var r in _hitTestRegions)
            {
                if (r.Contains(point))
                {
                    hit = true;
                    break;
                }
            }

            if (!hit)
            {
                return new IntPtr(HTTRANSPARENT);
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Same as screenshot/recording when not using a full selection region: punch a 1×1 px hole so
    /// Chromium/DWM does not treat SnipWindow as a full-screen occluder (YouTube/hardware video).
    /// Optionally merges the toolbar so it stays hit-testable.
    /// </summary>
    /// <remarks>
    /// When the toolbar is hidden we ALWAYS use disjoint islands / pass-through (video-friendly), regardless of the
    /// selection modifier. A near-full-client <c>SetWindowRgn</c> (as used when the toolbar is shown) makes
    /// Chromium/DWM treat the overlay as an occluder and stop compositing the browser underneath — a solid-gray
    /// tab/page switch. The cost is that a new drag-selection can't be started with the toolbar hidden: re-show it
    /// (F4 toggles globally) to restore full-client hit-testing.
    /// </remarks>
    private void ApplyTranslationDwmMinimalOccluderFix(IntPtr hwnd, double scaling, int windowWidth, int windowHeight)
    {
        if (_viewModel != null && !_viewModel.IsToolbarShownOnScreen)
        {
            // Toolbar hidden = the user switched to "read / use the page" mode. NEVER expand to the near-full-client
            // region here: a full-screen opaque SetWindowRgn makes Chromium/DWM treat this overlay as an occluder
            // and stop compositing the browser underneath, so switching browser tabs/pages paints a solid gray
            // frame (the reported bug). Previously the selection modifier being physically down still forced the
            // full-client region — which fires transiently for ANY browser Ctrl-shortcut (Ctrl+Tab / Ctrl+PgDn, via
            // the global Ctrl hook), and permanently when the modifier is set to "None". Always use the
            // video-friendly pass-through instead. Trade-off: starting a NEW drag-selection now requires re-showing
            // the toolbar (F4, which toggles globally) to restore full-client hit-testing — acceptable, since a
            // hidden toolbar signals the user is done selecting and wants to interact with the page.
            ApplyTranslationPassThroughExceptToolbarAndLoadingBar(hwnd, scaling);
            return;
        }

        Rect? toolbarRect = TryGetToolbarOpaqueRect(scaling, 8, out var computedToolbarRect)
            ? computedToolbarRect
            : null;

        Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0, toolbarRect, null);
    }

    /// <summary>
    /// Translation idle (no untranslated rings yet): only toolbar + top loading strip own hit-testing;
    /// the rest of the overlay passes clicks to windows below so the user can interact with the desktop.
    /// When the toolbar is hidden, a moderate transparent + click-through "anchor" rect keeps the window region
    /// non-degenerate so DWM does not gray a Chromium window underneath (see the hidden branch below).
    /// </summary>
    private void ApplyTranslationPassThroughExceptToolbarAndLoadingBar(IntPtr hwnd, double scaling)
    {
        var opaque = new System.Collections.Generic.List<Rect>();
        bool toolbarShown = TryGetToolbarOpaqueRect(scaling, 4, out var toolbarRect);
        if (toolbarShown)
        {
            opaque.Add(toolbarRect);
        }

        if (_viewModel != null && _viewModel.ShowTopLoadingBar)
        {
            foreach (var screen in _viewModel.AllScreenBounds)
            {
                opaque.Add(new Rect(screen.X * scaling, screen.Y * scaling, screen.W * scaling, 8 * scaling));
            }
        }

        if (!toolbarShown)
        {
            // Toolbar hidden: without a real on-screen region the overlay collapses to the ~1×1 stub below. A
            // near-degenerate window region on this transparent, top-most overlay corrupts the DWM composition of
            // a Chromium (GPU/DWM-composited) window underneath — the browser repaints solid gray on tab/page
            // switch. It only happens when the toolbar is hidden, because a SHOWN toolbar leaves a real region
            // island that keeps DWM composing normally. Emit a moderate "anchor" rect that reproduces that
            // shown-state footprint. Nothing is painted there while hidden (so it's invisible) and it is made
            // fully click-through below, so it does NOT reintroduce the un-clickable mask that #120 removed.
            opaque.Add(GetTranslationHiddenDwmAnchorRect(scaling));
        }

        // Keep a 1x1 region to avoid full-screen occluder heuristics while still allowing pass-through.
        opaque.Add(new Rect(0, 0, 1, 1));
        Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaque, null);
        _hitTestRegions.Clear();
        // Shown: the toolbar island owns clicks and the region itself defines pass-through (outside = click-through).
        // Hidden: the anchor rect is part of the window region (the DWM anchor) but must NOT block clicks — route
        // every point through the WM_NCHITTEST hook, which returns HTTRANSPARENT for anything not in
        // _hitTestRegions (empty here), making the whole overlay click-through.
        _useHitTestRegions = !toolbarShown;
    }

    /// <summary>
    /// A moderate on-screen rect used ONLY while the translation toolbar is hidden, to keep the Win32 window
    /// region non-degenerate so DWM keeps compositing a browser underneath (see
    /// <see cref="ApplyTranslationPassThroughExceptToolbarAndLoadingBar"/>). Toolbar-sized, anchored at the
    /// top-left corner (guaranteed on the primary monitor); rendered transparent and click-through, so invisible.
    /// </summary>
    private Rect GetTranslationHiddenDwmAnchorRect(double scaling)
    {
        double logicalW = _viewModel != null && _viewModel.ToolbarWidth > 1 ? _viewModel.ToolbarWidth : 220;
        double logicalH = _viewModel != null && _viewModel.ToolbarHeight > 1 ? _viewModel.ToolbarHeight : 52;
        return new Rect(0, 0, logicalW * scaling, logicalH * scaling);
    }

    private bool TryGetToolbarOpaqueRect(double scaling, double logicalPadding, out Rect rect)
    {
        rect = default;

        // The toolbar only contributes an opaque / hit-test island while the user can actually SEE it on
        // screen (ShowToolbar on). When hidden, emit no island — otherwise its old position stays masked and
        // the area underneath can't be interacted with (the reported bug). IsToolbarShownOnScreen is the
        // documented flag for Win32 hit-testing; IsToolbarVisible (used below) ignores ShowToolbar, so the
        // fallback would otherwise keep returning the cached toolbar rect after the toolbar was hidden.
        if (_viewModel == null || !_viewModel.IsToolbarShownOnScreen)
        {
            return false;
        }

        if (Toolbar == null || !Toolbar.IsVisible || Toolbar.Bounds.Width <= 1 || Toolbar.Bounds.Height <= 1)
        {
            if (!_viewModel.IsToolbarVisible || _viewModel.ToolbarWidth <= 1 || _viewModel.ToolbarHeight <= 1)
            {
                return false;
            }

            double tw = _viewModel.ToolbarWidth + (logicalPadding * 2);
            double th = _viewModel.ToolbarHeight + (logicalPadding * 2);
            double tx = _viewModel.ToolbarLeft - logicalPadding;
            double ty = _viewModel.ToolbarTop - logicalPadding;
            rect = new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling);
            return true;
        }

        var topLeft = Toolbar.TranslatePoint(new Point(0, 0), this);
        if (!topLeft.HasValue)
        {
            return false;
        }

        double paddedX = topLeft.Value.X - logicalPadding;
        double paddedY = topLeft.Value.Y - logicalPadding;
        double paddedWidth = Toolbar.Bounds.Width + (logicalPadding * 2);
        double paddedHeight = Toolbar.Bounds.Height + (logicalPadding * 2);

        rect = new Rect(
            Math.Max(0, paddedX) * scaling,
            Math.Max(0, paddedY) * scaling,
            paddedWidth * scaling,
            paddedHeight * scaling);
        return true;
    }

    /// <summary>
    /// Opaque UI islands for translation general (cursor) mode — same geometry as former WM_NCHITTEST list, for SetWindowRgn.
    /// </summary>
    private void CollectTranslationGeneralModeOpaqueRects(double scaling, System.Collections.Generic.List<Rect> dest)
    {
        if (_viewModel == null) return;

        if (TryGetToolbarOpaqueRect(scaling, 8, out var toolbarRect))
        {
            dest.Add(toolbarRect);
        }

        foreach (var sel in _viewModel.UserSelections)
        {
            if (sel.Bounds.Width <= 10 || sel.Bounds.Height <= 10) continue;
            var rect = sel.Bounds;

            if (sel.IsTranslated)
            {
                if (sel.IsAudioPanel)
                {
                    dest.Add(new Rect(
                        rect.X * scaling,
                        rect.Y * scaling,
                        rect.Width * scaling,
                        rect.Height * scaling));
                }
                else
                {
	                    dest.Add(new Rect(
	                        rect.X * scaling,
	                        rect.Y * scaling,
	                        rect.Width * scaling,
	                        rect.Height * scaling));
                }
            }
            else
            {
                dest.Add(new Rect(
                    rect.X * scaling,
                    rect.Y * scaling,
                    rect.Width * scaling,
                    rect.Height * scaling));
            }
        }

        if (_viewModel.ShowTopLoadingBar)
        {
            foreach (var screen in _viewModel.AllScreenBounds)
            {
                dest.Add(new Rect(
                    screen.X * scaling,
                    screen.Y * scaling,
                    screen.W * scaling,
                    8 * scaling));
            }
        }

        AddTranslatedBlockOverlayRegions(dest, scaling);
    }

    private void AddTranslatedBlockOverlayRegions(System.Collections.Generic.List<Rect> dest, double scaling)
    {
        if (_viewModel == null || _viewModel.TranslatedBlocks.Count == 0)
        {
            return;
        }

        if (TryAddTranslatedBlockVisualBounds(dest, scaling))
        {
            return;
        }

        const double maxOuterWidth = 400.0;
        const double contentWidth = 376.0;
        const double itemSpacing = 4.0;
        const double chromeHeight = 44.0;
        const double padding = 12.0;

        double x = _viewModel.TranslationOverlayLeft;
        double y = _viewModel.TranslationOverlayTop;
        double totalHeight = 0;

        foreach (var block in _viewModel.TranslatedBlocks)
        {
            string translated = block.TranslatedText ?? string.Empty;
            string original = block.OriginalText ?? string.Empty;
            double fontSize = block.DisplayFontSize > 0 ? block.DisplayFontSize : block.InferredFontSize;

            double translatedHeight = MeasureTranslatedBlockOverlayHeight(translated, fontSize, contentWidth);
            double originalHeight = MeasureTranslatedBlockOverlayHeight(original, fontSize, contentWidth);
            double contentHeight = Math.Max(translatedHeight, originalHeight);
            double itemHeight = Math.Max(chromeHeight, contentHeight + chromeHeight);
            totalHeight += itemHeight + itemSpacing;
        }

        if (totalHeight > 0)
        {
            totalHeight -= itemSpacing;
        }

        dest.Add(new Rect(
            Math.Max(0, x - padding) * scaling,
            Math.Max(0, y - padding) * scaling,
            (maxOuterWidth + (padding * 2)) * scaling,
            Math.Max(chromeHeight, totalHeight + (padding * 2)) * scaling));
    }

    private static double MeasureTranslatedBlockOverlayHeight(string text, double fontSize, double contentWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 24.0;
        }

        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = Math.Max(6.0, fontSize),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = contentWidth
        };

        textBlock.Measure(new Size(contentWidth, double.PositiveInfinity));
        return Math.Max(24.0, textBlock.DesiredSize.Height);
    }

    private bool TryAddTranslatedBlockVisualBounds(System.Collections.Generic.List<Rect> dest, double scaling)
    {
        if (TranslatedBlocksOverlay == null || !TranslatedBlocksOverlay.IsVisible)
        {
            return false;
        }

        Rect? union = null;
        foreach (var border in TranslatedBlocksOverlay.GetVisualDescendants().OfType<Border>())
        {
            if (!string.Equals(border.Name, "TranslationBorder", StringComparison.Ordinal))
            {
                continue;
            }

            var topLeft = border.TranslatePoint(new Point(0, 0), this);
            if (!topLeft.HasValue)
            {
                continue;
            }

            var rect = new Rect(topLeft.Value.X, topLeft.Value.Y, border.Bounds.Width, border.Bounds.Height);
            union = union.HasValue ? union.Value.Union(rect) : rect;
        }

        if (!union.HasValue || union.Value.Width <= 0 || union.Value.Height <= 0)
        {
            return false;
        }

        const double pad = 12.0;
        var padded = new Rect(
            Math.Max(0, union.Value.X - pad),
            Math.Max(0, union.Value.Y - pad),
            union.Value.Width + (pad * 2),
            union.Value.Height + (pad * 2));

        dest.Add(new Rect(
            padded.X * scaling,
            padded.Y * scaling,
            padded.Width * scaling,
            padded.Height * scaling));
        return true;
    }

    /// <summary>
    /// Same outer ring + inner pass-through hole as <see cref="SnipState.Selected"/> / screenshot (physical client pixels).
    /// </summary>
    private (Rect Outer, Rect InnerHole) ComputeScreenshotStyleRingPhysicalRects(Rect selectionBoundsLogical, double scaling)
    {
        var scaledRect = new Rect(
            selectionBoundsLogical.X * scaling,
            selectionBoundsLogical.Y * scaling,
            selectionBoundsLogical.Width * scaling,
            selectionBoundsLogical.Height * scaling);

        double maxMargin = 0;
        if (_viewModel != null)
        {
            double hSize = 40 * scaling;
            double sThick = 15 * scaling;
            double wW = _viewModel.WingWidth * scaling;
            double wH = _viewModel.WingHeight * scaling;
            double iSize = (_viewModel.SelectionIconSize + 8) * scaling;

            maxMargin = Math.Max(hSize / 2, Math.Max(sThick / 2, Math.Max(wW, iSize)));

            double verticalOverflow = (wH / 2) - (scaledRect.Height / 2);
            if (verticalOverflow > maxMargin)
            {
                maxMargin = verticalOverflow + (10 * scaling);
            }
        }
        else
        {
            maxMargin = 20 * scaling;
        }

        maxMargin += 20 * scaling;

        var outerBox = new Rect(
            scaledRect.X - maxMargin,
            scaledRect.Y - maxMargin,
            scaledRect.Width + maxMargin * 2,
            scaledRect.Height + maxMargin * 2);

        double innerShrink = 0;
        if (_viewModel != null)
        {
            double innerIconWidth = (_viewModel.SelectionIconSize + 4) * scaling;
            double borderThick = _viewModel.SelectionBorderThickness * scaling;
            innerShrink = borderThick + innerIconWidth + (12 * scaling);
            innerShrink = Math.Max(15 * scaling, innerShrink);
        }

        double maxAllowedShrink = Math.Min(scaledRect.Width, scaledRect.Height) / 2.0 - 1;
        if (maxAllowedShrink < 0) maxAllowedShrink = 0;
        innerShrink = Math.Min(innerShrink, maxAllowedShrink);

        var innerHole = new Rect(
            scaledRect.X + innerShrink,
            scaledRect.Y + innerShrink,
            Math.Max(0, scaledRect.Width - innerShrink * 2),
            Math.Max(0, scaledRect.Height - innerShrink * 2));

        return (outerBox, innerHole);
    }

    private void RequestTranslationWindowRegionRefresh()
    {
        if (_viewModel == null || !OperatingSystem.IsWindows()) return;
        UpdateWindowRegion(_viewModel.SelectionRect, _viewModel.CurrentState, _viewModel.IsDrawingMode);
    }

    /// <summary>
    /// Linux click-through, mirroring the Win32 pass-through: once a screenshot/recording region is
    /// confirmed, shape the overlay's X11 INPUT region to just the selection frame / handles / toolbar,
    /// so clicks inside the selection (and outside it) reach the windows beneath. Drawing and translation
    /// modes keep the whole overlay interactive (annotation / text selection need every pixel).
    /// </summary>
    private void UpdateInputShapeLinux(Rect selectionRect, SnipState state, bool isDrawingMode)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var window = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (window == IntPtr.Zero)
        {
            return;
        }

        bool isTranslation = _viewModel?.IsTranslationMode ?? false;
        bool selected = state == SnipState.Selected && selectionRect.Width > 10 && selectionRect.Height > 10;

        if (isDrawingMode || isTranslation || !selected)
        {
            GimmeCapture.Services.Platforms.Linux.LinuxWindowShape.ClearInputRegion(window);
            return;
        }

        double scaling = this.RenderScaling;
        var geometry = BuildSelectionInteractionGeometry(selectionRect);
        var rects = new List<PixelRect>();
        foreach (var r in geometry.EnumerateWindowRegionRects())
        {
            rects.Add(ToPhysicalPixelRect(r, scaling));
        }

        // TryGetToolbarOpaqueRect already returns physical pixels.
        if (TryGetToolbarOpaqueRect(scaling, 4, out var toolbarRect) && toolbarRect.Width > 0 && toolbarRect.Height > 0)
        {
            rects.Add(new PixelRect((int)toolbarRect.X, (int)toolbarRect.Y, (int)toolbarRect.Width, (int)toolbarRect.Height));
        }

        GimmeCapture.Services.Platforms.Linux.LinuxWindowShape.SetInputRegion(window, rects);
    }

    private static PixelRect ToPhysicalPixelRect(Rect logical, double scaling)
    {
        return new PixelRect(
            (int)Math.Floor(logical.X * scaling),
            (int)Math.Floor(logical.Y * scaling),
            (int)Math.Ceiling(logical.Width * scaling),
            (int)Math.Ceiling(logical.Height * scaling));
    }

    /// <summary>
    /// Updates the window region to create a "hole" in the selection area for mouse pass-through.
    /// This allows clicking on underlying windows (like YouTube) while keeping the border UI interactive.
    /// The hole is disabled when in drawing mode to allow annotations.
    /// </summary>
    private void UpdateWindowRegion(Rect selectionRect, SnipState state, bool isDrawingMode)
    {
        if (!OperatingSystem.IsWindows())
        {
            UpdateInputShapeLinux(selectionRect, state, isDrawingMode);
            return;
        }

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // Reset WM_NCHITTEST pass-through islands every refresh to avoid leaking translation-only
        // hit-test state into screenshot/recording region logic.
        _useHitTestRegions = false;
        _hitTestRegions.Clear();

        bool isTranslation = _viewModel?.IsTranslationMode ?? false;

        if (!isDrawingMode && (isTranslation || (state == SnipState.Selected && selectionRect.Width > 10 && selectionRect.Height > 10)))
        {
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);
            
            var holeRects = new System.Collections.Generic.List<Rect>();
            var extraRegions = new System.Collections.Generic.List<Rect>();
            var translationRings = new System.Collections.Generic.List<(Rect Outer, Rect InnerHole)>();

            if (isTranslation && _viewModel != null)
            {
                foreach (var sel in _viewModel.UserSelections)
                {
                    if (sel.Bounds.Width > 10 && sel.Bounds.Height > 10)
                    {
                        var rect = sel.Bounds;

                        if (sel.IsTranslated)
                        {
                            if (sel.IsAudioPanel)
                            {
                                // Audio panel keeps text inside fixed box (no extra text island below).
                                extraRegions.Add(new Rect(
                                    (rect.X - 20) * scaling,
                                    (rect.Y - 20) * scaling,
                                    (rect.Width + 40) * scaling,
                                    (rect.Height + 40) * scaling));
                            }
                            else
                            {
                                // 已翻譯：選取框及下方文字島嶼保持不透明
	                                extraRegions.Add(new Rect(
	                                    (rect.X - 20) * scaling,
	                                    (rect.Y - 20) * scaling,
	                                    (rect.Width + 40) * scaling,
	                                    (rect.Height + 40) * scaling));
                            }
                        }
                        else
                        {
                            // Translation behaves like screenshot/recording selection flow.
                            translationRings.Add(ComputeScreenshotStyleRingPhysicalRects(rect, scaling));
                        }
                    }
                }

                AddTranslatedBlockOverlayRegions(extraRegions, scaling);
            }

            // Translation single/multi (untranslated selection): same ring + inner hole as screenshot — not full-window-minus-holes.
            if (isTranslation && translationRings.Count > 0)
            {
                // Hold Ctrl: full client hit-test (same as no-selection) so the user can start a new rect outside the ring; release or finish drag restores rings.
                bool selModHeld = IsTranslationSelectionModifierDownForRegion();
                if (selModHeld && !_translationSuppressFullHitUntilSelectionModifierUp)
                {
                    ApplyTranslationDwmMinimalOccluderFix(hwnd, scaling, windowWidth, windowHeight);
                    return;
                }

                Rect? toolbarRectRing = TryGetToolbarOpaqueRect(scaling, 8, out var computedToolbarRectRing)
                    ? computedToolbarRectRing
                    : null;

                var ringsExtras = new System.Collections.Generic.List<Rect>(extraRegions);
                if (_viewModel != null && _viewModel.ShowTopLoadingBar)
                {
                    foreach (var screen in _viewModel.AllScreenBounds)
                    {
                        ringsExtras.Add(new Rect(
                            screen.X * scaling,
                            screen.Y * scaling,
                            screen.W * scaling,
                            8 * scaling));
                    }
                }

                Win32Helpers.SetMultipleBoundingBoxHoleRegions(hwnd, translationRings, toolbarRectRing, ringsExtras);
                return;
            }
            else if (state == SnipState.Selected)
            {
                var geometry = BuildSelectionInteractionGeometry(selectionRect);
                var opaque = new System.Collections.Generic.List<Rect>();
                bool lockSelectedSnapshot = _viewModel?.IsSelectionSnapshotLocked == true;

                if (lockSelectedSnapshot)
                {
                    opaque.Add(ScaleRect(selectionRect, scaling));
                }

                foreach (var rect in geometry.EnumerateWindowRegionRects())
                {
                    opaque.Add(ScaleRect(rect, scaling));
                }

                var hitTestRects = new System.Collections.Generic.List<Rect>();
                if (lockSelectedSnapshot)
                {
                    hitTestRects.Add(ScaleRect(selectionRect, scaling));
                }

                foreach (var rect in geometry.EnumerateHitTestRects())
                {
                    hitTestRects.Add(ScaleRect(rect, scaling));
                }

                Rect? selectedToolbarRect = TryGetToolbarOpaqueRect(scaling, 4, out var computedSelectedToolbarRect)
                    ? computedSelectedToolbarRect
                    : null;
                if (selectedToolbarRect.HasValue)
                {
                    hitTestRects.Add(selectedToolbarRect.Value);
                }

                // Keep the microscopic stub so Chromium/DWM still avoids treating this overlay
                // as a full-screen occluder while the rest of the window stays click-through.
                opaque.Add(new Rect(0, 0, 1, 1));
                Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaque, selectedToolbarRect);
                _hitTestRegions.Clear();
                _hitTestRegions.AddRange(hitTestRects);
                _useHitTestRegions = _hitTestRegions.Count > 0;
                return;
            }

            // Normal logic for Translation Mode
            // V8 修正：翻譯模式下即便 holeRects 為空（全部已翻譯），
            // 也要正確設定 region（只有 extraRegions 的情況）
            if (holeRects.Count == 0 && extraRegions.Count == 0)
            {
                if (isTranslation)
                {
                    // No selection yet: pass-through everywhere except toolbar/loading so the user can operate
                    // underlying apps; hold Ctrl to switch to full-window hit (see RequestTranslationWindowRegionRefresh).
                    bool selModHeld = IsTranslationSelectionModifierDownForRegion();
                    if (selModHeld && !_translationSuppressFullHitUntilSelectionModifierUp)
                    {
                        ApplyTranslationDwmMinimalOccluderFix(hwnd, scaling, windowWidth, windowHeight);
                        _useHitTestRegions = false;
                    }
                    else
                    {
                        ApplyTranslationPassThroughExceptToolbarAndLoadingBar(hwnd, scaling);
                    }
                }
                else
                {
                    Win32Helpers.ClearWindowRegion(hwnd);
                }
                return;
            }

            Rect? toolbarRect = TryGetToolbarOpaqueRect(scaling, 8, out var computedToolbarRect2)
                ? computedToolbarRect2
                : null;
            if (toolbarRect.HasValue)
            {
                extraRegions.Add(toolbarRect.Value);
            }

            // V13: Ensure TopLoadingBar is visible in Translation Mode (including General Mode)
            if (_viewModel != null && _viewModel.ShowTopLoadingBar)
            {
                foreach (var screen in _viewModel.AllScreenBounds)
                {
                    extraRegions.Add(new Rect(
                        screen.X * scaling, 
                        screen.Y * scaling, 
                        screen.W * scaling, 
                        8 * scaling)); // Increased height slightly for visibility safety
                }
            }

            int borderWidth = (int)((_viewModel?.SelectionBorderThickness ?? 6) * scaling);

            // Translation remainder: translated-only islands (+ toolbar/loading already in extraRegions).
            // Must NOT use ApplyTranslationDwmMinimalOccluderFix here — that path uses almost the full client
            // as SetWindowRgn opaque area (full window minus a 1×1 hole), which occludes YouTube/hardware video
            // under the overlay. Disjoint islands + 1×1 DWM stub matches idle translation behavior.
            if (isTranslation && holeRects.Count == 0)
            {
                bool selModHeld = IsTranslationSelectionModifierDownForRegion();
                if (selModHeld && !_translationSuppressFullHitUntilSelectionModifierUp)
                {
                    ApplyTranslationDwmMinimalOccluderFix(hwnd, scaling, windowWidth, windowHeight);
                    _useHitTestRegions = false;
                    return;
                }

                var opaque = new System.Collections.Generic.List<Rect>(extraRegions);
                opaque.Add(new Rect(0, 0, 1, 1));
                Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaque, null);
                _hitTestRegions.Clear();
                _hitTestRegions.AddRange(extraRegions);
                _useHitTestRegions = _hitTestRegions.Count > 0;
                return;
            }

            Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, holeRects, borderWidth, toolbarRect, extraRegions);
        }
        else
        {
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);

            if (!isTranslation)
            {
                // To prevent Chromium-based browsers (Edge/Chrome) from aggressively 
                // occluding YouTube/hardware-accelerated videos behind the SnipWindow initially,
                // we punch a microscopic 1x1 pixel hole at the top left.
                // This breaks the "full screen occluder" heuristic in the Desktop Window Manager (DWM).
                Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0);
            }
            else
            {
                // Translation + drawing: pointer must hit the whole overlay; disjoint-only would break ink outside tiny regions.
                // (When toolbar is hidden, ApplyTranslationDwmMinimalOccluderFix would route to pass-through — do not use it here.)
                Rect? drawToolbarRect = null;
                if (_viewModel != null && _viewModel.IsToolbarVisible && _viewModel.ToolbarWidth > 0 && _viewModel.ToolbarHeight > 0)
                {
                    double dtw = TranslationToolbarOpaqueWidthDip(_viewModel.ToolbarWidth + 40);
                    double dth = _viewModel.ToolbarHeight + 40;
                    double dtx = Math.Max(0, _viewModel.ToolbarLeft - 20);
                    double dty = Math.Max(0, _viewModel.ToolbarTop - 20);
                    drawToolbarRect = new Rect(dtx * scaling, dty * scaling, dtw * scaling, dth * scaling);
                }

                Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, new[] { new Rect(0, 0, 1, 1) }, 0, drawToolbarRect, null);
            }
        }
    }
}
