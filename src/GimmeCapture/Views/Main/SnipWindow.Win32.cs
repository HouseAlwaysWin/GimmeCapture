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

        // Only apply region when:
        // 1. In Selected state with valid selection
        // 2. NOT in drawing mode (SetWindowRgn hole prevents ANY window from receiving mouse in that area)
        if (state == SnipState.Selected && selectionRect.Width > 10 && selectionRect.Height > 10 && !isDrawingMode)
        {
            // Get physical pixel dimensions (account for DPI scaling)
            double scaling = this.RenderScaling;
            int windowWidth = (int)(this.Bounds.Width * scaling);
            int windowHeight = (int)(this.Bounds.Height * scaling);
            
            // Convert selection rect to physical pixels
            var scaledRect = new Rect(
                selectionRect.X * scaling,
                selectionRect.Y * scaling,
                selectionRect.Width * scaling,
                selectionRect.Height * scaling
            );
            
            // Calculate toolbar rect in physical pixels (prevents toolbar from being clipped)
            Rect? toolbarRect = null;
            if (_viewModel != null)
            {
                // Toolbar position is stored in ViewModel, size is approximately 400x40
                const double toolbarWidth = 500;  // Slightly larger to account for flyouts
                const double toolbarHeight = 50;
                toolbarRect = new Rect(
                    _viewModel.ToolbarLeft * scaling,
                    _viewModel.ToolbarTop * scaling,
                    toolbarWidth * scaling,
                    toolbarHeight * scaling
                );
            }

            // EXTRA OPAQUE REGIONS: Wings and Handles
            // We must add handles back because narrowing the borderWidth makes them part of the hole (non-interactive)
            var extraRegions = new System.Collections.Generic.List<Rect>();
            if (_viewModel != null)
            {
                // 1. Wings (centered vertically on selection edges)
                double wingsY = selectionRect.Center.Y - (_viewModel.WingHeight / 2);
                extraRegions.Add(new Rect((selectionRect.X - _viewModel.WingWidth) * scaling, wingsY * scaling, _viewModel.WingWidth * scaling, _viewModel.WingHeight * scaling));
                extraRegions.Add(new Rect(selectionRect.Right * scaling, wingsY * scaling, _viewModel.WingWidth * scaling, _viewModel.WingHeight * scaling));

                // 2. Corner Handles (30x30, centered on corners)
                double hSize = 30 * scaling;
                double hHalf = 15 * scaling;
                extraRegions.Add(new Rect(scaledRect.X - hHalf, scaledRect.Y - hHalf, hSize, hSize)); // TL
                extraRegions.Add(new Rect(scaledRect.Right - hHalf, scaledRect.Y - hHalf, hSize, hSize)); // TR
                extraRegions.Add(new Rect(scaledRect.X - hHalf, scaledRect.Bottom - hHalf, hSize, hSize)); // BL
                extraRegions.Add(new Rect(scaledRect.Right - hHalf, scaledRect.Bottom - hHalf, hSize, hSize)); // BR

                // 2b. Corner Decoration Icons (hearts/skulls) - positioned 4px inside selection with SelectionIconSize
                // Add extra padding (8px) to ensure entire icon is visible including any anti-aliasing
                double iconSize = (_viewModel.SelectionIconSize + 8) * scaling;
                double iconMargin = 2 * scaling; // Reduce margin to capture more of the icon
                extraRegions.Add(new Rect(scaledRect.X + iconMargin, scaledRect.Y + iconMargin, iconSize, iconSize)); // TL heart
                extraRegions.Add(new Rect(scaledRect.Right - iconMargin - iconSize, scaledRect.Y + iconMargin, iconSize, iconSize)); // TR skull
                extraRegions.Add(new Rect(scaledRect.X + iconMargin, scaledRect.Bottom - iconMargin - iconSize, iconSize, iconSize)); // BL heart
                extraRegions.Add(new Rect(scaledRect.Right - iconMargin - iconSize, scaledRect.Bottom - iconMargin - iconSize, iconSize, iconSize)); // BR skull

                // 3. Side Handles (15px thick)
                double sThick = 15 * scaling;
                double sHalf = 7.5 * scaling;
                extraRegions.Add(new Rect(scaledRect.X + hSize, scaledRect.Y - sHalf, scaledRect.Width - hSize * 2, sThick)); // Top
                extraRegions.Add(new Rect(scaledRect.X + hSize, scaledRect.Bottom - sHalf, scaledRect.Width - hSize * 2, sThick)); // Bottom
                extraRegions.Add(new Rect(scaledRect.X - sHalf, scaledRect.Y + hSize, sThick, scaledRect.Height - hSize * 2)); // Left
                extraRegions.Add(new Rect(scaledRect.Right - sHalf, scaledRect.Y + hSize, sThick, scaledRect.Height - hSize * 2)); // Right
            }
            
            // Apply window region with hole.
            // Use small borderWidth (2px) to match visual border, keep entire inner area clear.
            int borderWidth = (int)(2 * scaling);
            Win32Helpers.SetWindowHoleRegion(hwnd, windowWidth, windowHeight, scaledRect, borderWidth, toolbarRect, extraRegions);
        }
        else
        {
            // Clear region when not in Selected state OR in drawing mode
            // Drawing mode requires full window for mouse capture
            Win32Helpers.ClearWindowRegion(hwnd);
        }
    }

    /// <summary>
    /// Captures a snapshot of the selection area before closing the hole.
    /// This allows the user to see what they're annotating while in drawing mode.
    /// Optimized to use WriteableBitmap and raw pointer copy instead of intermediate GDI+ Bitmap/MemoryStream.
    /// </summary>

}
