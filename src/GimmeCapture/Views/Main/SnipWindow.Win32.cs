using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels.Main;
using System;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
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
                foreach (var sel in _viewModel.UserSelections)
                {
                    if (sel.Bounds.Width > 10 && sel.Bounds.Height > 10)
                    {
                        var rect = sel.Bounds;
                        // 1. Hole (The selection box itself)
                        holeRects.Add(new Rect(rect.X * scaling, rect.Y * scaling, rect.Width * scaling, rect.Height * scaling));

                        // 2. Opaque Island: External Drag Handle (Top 20px outside V6)
                        // Height matches XAML 18px + margin
                        extraRegions.Add(new Rect(rect.X * scaling, (rect.Y - 20) * scaling, rect.Width * scaling, 20 * scaling));

                        // 3. Opaque Island: Text Result (Safe Mapping)
                        if (sel.IsTranslated)
                        {
                            // Ensure island doesn't exceed box bounds or become negative
                            double txtMargin = 4 * scaling;
                            double safeW = Math.Max(0, rect.Width * scaling - txtMargin * 2);
                            double safeH = Math.Max(0, rect.Height * scaling - txtMargin * 2);
                            extraRegions.Add(new Rect((rect.X * scaling) + txtMargin, (rect.Y * scaling) + txtMargin, safeW, safeH));
                        }

                        // 4. Corner Handles (Invisible but opaque for Hit-Testing/Mouse cursor)
                        double hSize = 16 * scaling;
                        double hHalf = 8 * scaling;
                        extraRegions.Add(new Rect((rect.X - hHalf) * scaling, (rect.Y - hHalf) * scaling, hSize, hSize)); // TL
                        extraRegions.Add(new Rect((rect.Right - hHalf) * scaling, (rect.Y - hHalf) * scaling, hSize, hSize)); // TR
                        extraRegions.Add(new Rect((rect.X - hHalf) * scaling, (rect.Bottom - hHalf) * scaling, hSize, hSize)); // BL
                        extraRegions.Add(new Rect((rect.Right - hHalf) * scaling, (rect.Bottom - hHalf) * scaling, hSize, hSize)); // BR
                    }
                }
            }
            else if (state == SnipState.Selected)
            {
                var scaledRect = new Rect(selectionRect.X * scaling, selectionRect.Y * scaling, selectionRect.Width * scaling, selectionRect.Height * scaling);
                holeRects.Add(scaledRect);

                if (_viewModel != null)
                {
                    double wingsY = selectionRect.Center.Y - (_viewModel.WingHeight / 2);
                    extraRegions.Add(new Rect((selectionRect.X - _viewModel.WingWidth) * scaling, wingsY * scaling, _viewModel.WingWidth * scaling, _viewModel.WingHeight * scaling));
                    extraRegions.Add(new Rect(selectionRect.Right * scaling, wingsY * scaling, _viewModel.WingWidth * scaling, _viewModel.WingHeight * scaling));

                    double hSize = 30 * scaling;
                    double hHalf = 15 * scaling;
                    extraRegions.Add(new Rect(scaledRect.X - hHalf, scaledRect.Y - hHalf, hSize, hSize));
                    extraRegions.Add(new Rect(scaledRect.Right - hHalf, scaledRect.Y - hHalf, hSize, hSize));
                    extraRegions.Add(new Rect(scaledRect.X - hHalf, scaledRect.Bottom - hHalf, hSize, hSize));
                    extraRegions.Add(new Rect(scaledRect.Right - hHalf, scaledRect.Bottom - hHalf, hSize, hSize));

                    double iconSize = (_viewModel.SelectionIconSize + 8) * scaling;
                    double iconMargin = 2 * scaling;
                    extraRegions.Add(new Rect(scaledRect.X + iconMargin, scaledRect.Y + iconMargin, iconSize, iconSize));
                    extraRegions.Add(new Rect(scaledRect.Right - iconMargin - iconSize, scaledRect.Y + iconMargin, iconSize, iconSize));
                    extraRegions.Add(new Rect(scaledRect.X + iconMargin, scaledRect.Bottom - iconMargin - iconSize, iconSize, iconSize));
                    extraRegions.Add(new Rect(scaledRect.Right - iconMargin - iconSize, scaledRect.Bottom - iconMargin - iconSize, iconSize, iconSize));

                    double sThick = 15 * scaling;
                    double sHalf = 7.5 * scaling;
                    extraRegions.Add(new Rect(scaledRect.X + hSize, scaledRect.Y - sHalf, scaledRect.Width - hSize * 2, sThick));
                    extraRegions.Add(new Rect(scaledRect.X + hSize, scaledRect.Bottom - sHalf, scaledRect.Width - hSize * 2, sThick));
                    extraRegions.Add(new Rect(scaledRect.X - sHalf, scaledRect.Y + hSize, sThick, scaledRect.Height - hSize * 2));
                    extraRegions.Add(new Rect(scaledRect.Right - sHalf, scaledRect.Y + hSize, sThick, scaledRect.Height - hSize * 2));
                }
            }

            if (holeRects.Count == 0)
            {
                Win32Helpers.ClearWindowRegion(hwnd);
                return;
            }

            Rect? toolbarRect = null;
            if (_viewModel != null && _viewModel.ToolbarWidth > 0)
            {
                double tw = _viewModel.ToolbarWidth + 20; 
                double th = _viewModel.ToolbarHeight + 20;
                toolbarRect = new Rect((_viewModel.ToolbarLeft - 2) * scaling, (_viewModel.ToolbarTop - 2) * scaling, tw * scaling, th * scaling);
            }
            
            int borderWidth = (int)(6 * scaling);
            Win32Helpers.SetMultiWindowHoleRegion(hwnd, windowWidth, windowHeight, holeRects, borderWidth, toolbarRect, extraRegions);
        }
        else
        {
            Win32Helpers.ClearWindowRegion(hwnd);
        }
    }

}
