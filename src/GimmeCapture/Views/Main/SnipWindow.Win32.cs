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

                        if (sel.IsTranslated)
                        {
                            // 已翻譯：不挖洞，整塊保持不透明
                            // 選取模式時不含拖拽把手區域
                            bool includeHandle = !(_viewModel.IsTranslationSelectionActive);
                            double handleOffset = includeHandle ? 20 : 0;
                            extraRegions.Add(new Rect(
                                rect.X * scaling,
                                (rect.Y - handleOffset) * scaling,
                                rect.Width * scaling,
                                (rect.Height + handleOffset) * scaling));
                        }
                        else
                        {
                            // 未翻譯：挖洞穿透
                            holeRects.Add(new Rect(rect.X * scaling, rect.Y * scaling, rect.Width * scaling, rect.Height * scaling));

                            // 選取模式時不加拖拽把手 extra region
                            if (!_viewModel.IsTranslationSelectionActive)
                            {
                                extraRegions.Add(new Rect(rect.X * scaling, (rect.Y - 20) * scaling, rect.Width * scaling, 20 * scaling));
                            }

                            // Corner Handles
                            double hSize = 24 * scaling;
                            double hHalf = hSize / 2;
                            extraRegions.Add(new Rect(rect.X * scaling - hHalf, rect.Y * scaling - hHalf, hSize, hSize));
                            extraRegions.Add(new Rect(rect.Right * scaling - hHalf, rect.Y * scaling - hHalf, hSize, hSize));
                            extraRegions.Add(new Rect(rect.X * scaling - hHalf, rect.Bottom * scaling - hHalf, hSize, hSize));
                            extraRegions.Add(new Rect(rect.Right * scaling - hHalf, rect.Bottom * scaling - hHalf, hSize, hSize));
                        }
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
