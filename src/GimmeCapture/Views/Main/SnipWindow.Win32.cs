using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    // Win32 Interop for click-through
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _oldWndProc;

    private List<Rect> _hitTestRegions = new();
    private bool _useHitTestRegions = false;

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
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST && _useHitTestRegions && !(_viewModel?.IsDrawingMode ?? false))
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            
            var pPos = this.Position;
            double winX = x - pPos.X;
            double winY = y - pPos.Y;
            var point = new Point(winX, winY);

            bool hit = false;
            foreach (var r in _hitTestRegions)
            {
                if (r.Contains(point))
                {
                    hit = true;
                    break;
                }
            }

            // Also check if inside the selection box (since users might want to copy/save without clicking through)
            // Wait, the user wants the entire screen to be click-through EXCEPT the borders.
            // But we actually DO want the click to pass through to desktop inside the selection box as well!
            // Wait! If the user clicks "inside", the user is trying to record or click on YouTube! So it should pass through.
            // But if the user wants to draw annotations, IsDrawingMode is true, so this whole block is skipped. Perfect!
            if (!hit)
            {
                return new IntPtr(HTTRANSPARENT);
            }
        }
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Updates the window region to create a "hole" in the selection area for mouse pass-through.
    /// This allows clicking on underlying windows (like YouTube) while keeping the border UI interactive.
    /// The hole is disabled when in drawing mode to allow annotations.
    /// </summary>
    private void UpdateWindowRegion(Rect selectionRect, SnipState state, bool isDrawingMode)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        bool isTranslation = _viewModel?.IsTranslationMode ?? false;

        if (!isDrawingMode && (isTranslation || (state == SnipState.Selected && selectionRect.Width > 10 && selectionRect.Height > 10)))
        {
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);
            
            var holeRects = new System.Collections.Generic.List<Rect>();
            var extraRegions = new System.Collections.Generic.List<Rect>();

            if (isTranslation && _viewModel != null)
            {
                bool isEditMode = !_viewModel.IsTranslationSelectionActive;

                if (isEditMode)
                {
                    // V8: Edit mode, screen is transparent, leaving ONLY the translation blocks completely opaque!
                    holeRects.Add(new Rect(0, 0, windowWidth, windowHeight));
                }

                foreach (var sel in _viewModel.UserSelections)
                {
                    if (sel.Bounds.Width > 10 && sel.Bounds.Height > 10)
                    {
                        var rect = sel.Bounds;

                        if (sel.IsTranslated)
                        {
                            // 已翻譯：選取框及下方文字島嶼保持不透明
                            extraRegions.Add(new Rect(
                                rect.X * scaling,
                                rect.Y * scaling,
                                rect.Width * scaling,
                                (rect.Height + sel.EstimatedTextHeight + 20) * scaling));
                        }
                        else
                        {
                            if (isEditMode)
                            {
                                // 未翻譯且在編輯模式下，框選區域依然要能互動(例如刪除)
                                extraRegions.Add(new Rect(
                                    rect.X * scaling,
                                    rect.Y * scaling,
                                    rect.Width * scaling,
                                    rect.Height * scaling));
                            }
                            else
                            {
                                // 未翻譯且在選取模式：挖洞穿透，允許滑鼠繪圖或截取
                                // 縮小洞口以保留內圈的拖拉把手環 (MoveHandle Border, 20px)
                                double shrink = 20 * scaling;
                                holeRects.Add(new Rect(
                                    rect.X * scaling + shrink, 
                                    rect.Y * scaling + shrink, 
                                    Math.Max(0, rect.Width * scaling - shrink * 2), 
                                    Math.Max(0, rect.Height * scaling - shrink * 2)));

                                // Corner Handles
                                double hSize = 40 * scaling;
                                double hHalf = hSize / 2;
                                extraRegions.Add(new Rect(rect.X * scaling - hHalf, rect.Y * scaling - hHalf, hSize, hSize));
                                extraRegions.Add(new Rect(rect.Right * scaling - hHalf, rect.Y * scaling - hHalf, hSize, hSize));
                                extraRegions.Add(new Rect(rect.X * scaling - hHalf, rect.Bottom * scaling - hHalf, hSize, hSize));
                                extraRegions.Add(new Rect(rect.Right * scaling - hHalf, rect.Bottom * scaling - hHalf, hSize, hSize));

                                // Edge Strips (20px) for drag + right-click
                                double e = 20 * scaling;
                                extraRegions.Add(new Rect(rect.X * scaling - e/2, rect.Y * scaling, e, rect.Height * scaling));         // Left
                                extraRegions.Add(new Rect(rect.Right * scaling - e/2, rect.Y * scaling, e, rect.Height * scaling));     // Right
                                extraRegions.Add(new Rect(rect.X * scaling, rect.Y * scaling - e/2, rect.Width * scaling, e));          // Top
                                extraRegions.Add(new Rect(rect.X * scaling, rect.Bottom * scaling - e/2, rect.Width * scaling, e));     // Bottom
                            }
                        }
                    }
                }
            }
            else if (state == SnipState.Selected)
            {
                // V8 Fix: Use a single contiguous Bounding Box region with an inner hole!
                // This allows full-screen pass-through outside the box AND avoids the DWM shadow 
                // glitches caused by combining multiple disjoint `extraRegions` for wings/skulls.
                var scaledRect = new Rect(selectionRect.X * scaling, selectionRect.Y * scaling, selectionRect.Width * scaling, selectionRect.Height * scaling);
                
                // Calculate outer bounding box that firmly wraps the selection + all wings/borders
                double maxMargin = 0;
                if (_viewModel != null)
                {
                    double hSize = 40 * scaling;      // Handles
                    double sThick = 15 * scaling;     // Frame edges
                    double wW = _viewModel.WingWidth * scaling;
                    double wH = _viewModel.WingHeight * scaling;
                    double iSize = (_viewModel.SelectionIconSize + 8) * scaling; 
                    
                    // Base margin handles basic widths
                    maxMargin = Math.Max(hSize / 2, Math.Max(sThick / 2, Math.Max(wW, iSize)));
                    
                    // Account for vertical overflowing wings on short selections
                    double verticalOverflow = (wH / 2) - (scaledRect.Height / 2);
                    if (verticalOverflow > maxMargin)
                    {
                        maxMargin = verticalOverflow + (10 * scaling); // +10px safety buffer
                    }
                }
                else
                {
                    maxMargin = 20 * scaling;
                }
                
                // Add an extra safety buffer for high-DPI or slight rounding variations
                maxMargin += 20 * scaling;
                
                // The single contiguous outer region containing our graphics (avoids slicing complex PNG anti-aliasing)
                var outerBox = new Rect(
                    scaledRect.X - maxMargin,
                    scaledRect.Y - maxMargin,
                    scaledRect.Width + maxMargin * 2,
                    scaledRect.Height + maxMargin * 2
                );

                Rect? selectedToolbarRect = null;
                if (_viewModel != null && _viewModel.ToolbarWidth > 0)
                {
                    double tw = _viewModel.ToolbarWidth + 20; 
                    double th = _viewModel.ToolbarHeight + 20;
                    selectedToolbarRect = new Rect((_viewModel.ToolbarLeft - 2) * scaling, (_viewModel.ToolbarTop - 2) * scaling, tw * scaling, th * scaling);
                }

                // Avalonia draws borders and corners INSIDE the selectionRect Canvas.
                // Punching a hole exactly at scaledRect deletes them! 
                // We must shrink (deflate) the inner hole by the max inner penetration of our UI objects (~30px)
                double innerShrink = 0;
                if (_viewModel != null)
                {
                    // Corners penetrate by IconSize AND they are pushed inwards by a Margin proportional to BorderThickness.
                    double innerIconWidth = (_viewModel.SelectionIconSize + 4) * scaling; 
                    double borderThick = _viewModel.SelectionBorderThickness * scaling;
                    
                    // The inner clipping point is Additive (BorderThickness + IconSize + Safety Buffer)
                    innerShrink = borderThick + innerIconWidth + (12 * scaling);
                    innerShrink = Math.Max(15 * scaling, innerShrink); // At least 15px for basic UI handles
                }
                
                // Safe guard: Do not shrink the hole so much that it inverts the geometry natively
                double maxAllowedShrink = Math.Min(scaledRect.Width, scaledRect.Height) / 2.0 - 1;
                if (maxAllowedShrink < 0) maxAllowedShrink = 0;
                innerShrink = Math.Min(innerShrink, maxAllowedShrink);

                var innerHole = new Rect(
                    scaledRect.X + innerShrink,
                    scaledRect.Y + innerShrink,
                    Math.Max(0, scaledRect.Width - innerShrink * 2),
                    Math.Max(0, scaledRect.Height - innerShrink * 2)
                );

                Win32Helpers.SetBoundingBoxHoleRegion(hwnd, outerBox, innerHole, selectedToolbarRect);
                return;
            }

            // Normal logic for Translation Mode
            _useHitTestRegions = false;

            // V8 修正：翻譯模式下即便 holeRects 為空（全部已翻譯），
            // 也要正確設定 region（只有 extraRegions 的情況）
            if (holeRects.Count == 0 && extraRegions.Count == 0)
            {
                Win32Helpers.ClearWindowRegion(hwnd);
                return;
            }

            Rect? toolbarRect = null;
            if (_viewModel != null && _viewModel.ToolbarWidth > 0)
            {
                // V13: Robust toolbar region calculation
                double tw = _viewModel.ToolbarWidth + 40; // More padding
                double th = _viewModel.ToolbarHeight + 40;
                double tx = _viewModel.ToolbarLeft - 20;
                double ty = _viewModel.ToolbarTop - 20;

                toolbarRect = new Rect(tx * scaling, ty * scaling, tw * scaling, th * scaling);
                
                // Add to extraRegions to be absolutely sure it's opaque in SetMultiWindowHoleRegion
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
            if (isTranslation && _viewModel != null && !_viewModel.IsTranslationSelectionActive)
            {
                 // Edit mode removes the border around the hole
                 borderWidth = 0;
            }

            Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, holeRects, borderWidth, toolbarRect, extraRegions);
        }
        else
        {
            Win32Helpers.ClearWindowRegion(hwnd);
        }
    }

}
