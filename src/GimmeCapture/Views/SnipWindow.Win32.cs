using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Interop;
using GimmeCapture.ViewModels;
using System;

namespace GimmeCapture.Views;

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

            // EXTRA OPAQUE REGIONS: Wings
            // Wings are centered vertically on the selection edges
            var extraRegions = new System.Collections.Generic.List<Rect>();
            if (_viewModel != null)
            {
                double wingsY = selectionRect.Center.Y - (_viewModel.WingHeight / 2);
                
                // Left Wing (outside, flush)
                extraRegions.Add(new Rect(
                    (selectionRect.X - _viewModel.WingWidth) * scaling,
                    wingsY * scaling,
                    _viewModel.WingWidth * scaling,
                    _viewModel.WingHeight * scaling
                ));
                
                // Right Wing (outside, flush)
                extraRegions.Add(new Rect(
                    selectionRect.Right * scaling,
                    wingsY * scaling,
                    _viewModel.WingWidth * scaling,
                    _viewModel.WingHeight * scaling
                ));
            }
            
            // Apply window region with hole.
            // Use 30px logical border (matching handles) instead of 120px to reduce dead zone.
            int borderWidth = (int)(30 * scaling);
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
    private void CaptureDrawingModeSnapshot()
    {
        if (_viewModel == null || !OperatingSystem.IsWindows()) return;
        
        var selectionRect = _viewModel.SelectionRect;
        if (selectionRect.Width < 10 || selectionRect.Height < 10) return;

        try
        {
            // Calculate physical pixels for the selection area
            double scaling = this.RenderScaling;
            var screenPos = this.Position; // physical pixels
            
            // Convert selection logical coordinates to physical and add window physical position
            int xPhysical = (int)(selectionRect.X * scaling) + screenPos.X;
            int yPhysical = (int)(selectionRect.Y * scaling) + screenPos.Y;
            int widthPhysical = (int)(selectionRect.Width * scaling);
            int heightPhysical = (int)(selectionRect.Height * scaling);

            if (widthPhysical <= 0 || heightPhysical <= 0) return;

            // Use WriteableBitmap to avoid MemoryStream & PNG Encoding overhead
            var writeableBitmap = new WriteableBitmap(
                new PixelSize(widthPhysical, heightPhysical), 
                new Vector(96, 96), 
                PixelFormat.Bgra8888, 
                AlphaFormat.Premul);

            using (var lockedBitmap = writeableBitmap.Lock())
            {
                // We still use GDI+ to capture the screen, but we copy bits directly to the WriteableBitmap
                using var screenBmp = new System.Drawing.Bitmap(widthPhysical, heightPhysical, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(screenBmp))
                {
                    g.CopyFromScreen(
                        xPhysical, 
                        yPhysical, 
                        0, 0, 
                        new System.Drawing.Size(widthPhysical, heightPhysical));
                }

                var bmpData = screenBmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, widthPhysical, heightPhysical),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                // Copy memory
                // Stride might strictly differ usually but for 32bpp it is Width*4 generally.
                // Safest to copy line by line if strides differ.
                for (int y = 0; y < heightPhysical; y++)
                {
                   // Source Row
                   IntPtr srcRow = bmpData.Scan0 + (y * bmpData.Stride);
                   // Dest Row
                   IntPtr destRow = lockedBitmap.Address + (y * lockedBitmap.RowBytes);
                   
                   unsafe
                   {
                       Buffer.MemoryCopy(
                           (void*)srcRow, 
                           (void*)destRow, 
                           lockedBitmap.RowBytes, 
                           widthPhysical * 4);
                   }
                }

                screenBmp.UnlockBits(bmpData);
            }
            
            // Dispose old snapshot if exists to be safe
            _viewModel.DrawingModeSnapshot?.Dispose();
            _viewModel.DrawingModeSnapshot = writeableBitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to capture drawing mode snapshot: {ex.Message}");
        }
    }
}
