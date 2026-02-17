using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System;
using System.Threading.Tasks;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using System.Linq;
using System.Collections.Generic;

namespace GimmeCapture.Views.Floating;

public partial class FloatingVideoWindow : FloatingWindowBase
{
    public FloatingVideoWindow()
    {
        InitializeComponent();
        
        // Base constructor handles Event Handler registration
        
        PositionChanged += (s, e) => UpdateToolbarClamping();
    }
    
    protected override Control? GetContentControl() => this.FindControl<Image>("PinnedVideo");

    protected override Bitmap? GetContentSnapshot()
    {
        if (DataContext is FloatingVideoViewModel vm && vm.VideoBitmap is { } videoBitmap)
        {
            try 
            {
                 using var locked = videoBitmap.Lock();
                 var clone = new WriteableBitmap(videoBitmap.PixelSize, videoBitmap.Dpi, videoBitmap.Format, videoBitmap.AlphaFormat);
                 using (var destLock = clone.Lock())
                 {
                     unsafe { Buffer.MemoryCopy((void*)locked.Address, (void*)destLock.Address, (long)destLock.RowBytes * clone.PixelSize.Height, (long)locked.RowBytes * videoBitmap.PixelSize.Height); }
                 }
                 return clone;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error snapshotting video: {ex.Message}");
                return null;
            }
        }
        return null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingVideoViewModel vm)
        {
            // Video Specific VM Setup
            vm.RequestRedraw = () => 
            {
                var image = GetContentControl();
                image?.InvalidateVisual();
            };

            // FIX: Ensure the ViewModel uses THIS window's StorageProvider for saving files.
            vm.PickSaveFileAction = async () =>
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = GimmeCapture.Services.Core.Infrastructure.LocalizationService.Instance["SaveVideo"],
                    DefaultExtension = System.IO.Path.GetExtension(vm.VideoPath).TrimStart('.'),
                    ShowOverwritePrompt = true,
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.gif", "*.webm" } },
                        new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                    }
                });

                return file?.Path.LocalPath;
            };
        }
    }

    private void UpdateToolbarClamping()
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            if (!vm.ShowToolbar) return;

            var screen = Screens.ScreenFromWindow(this);
            if (screen != null)
            {
                double scaling = screen.Scaling;
                
                // Position.Y is physical pixels. Bounds.Height is logical pixels.
                // WindowBottom = Physical Top + (Logical Height * Scaling)
                double windowBottomPhysical = Position.Y + (Bounds.Height * scaling);
                double screenBottomPhysical = screen.WorkingArea.Bottom;

                // Default Margin in VM is 10.
                double defaultBottomMargin = 10;
                
                // If Window Bottom is below Screen Bottom, we need to push the toolbar UP.
                if (windowBottomPhysical > screenBottomPhysical)
                {
                    double overlapPhysical = windowBottomPhysical - screenBottomPhysical;
                    double overlapLogical = overlapPhysical / scaling;
                    
                    double newBottomMargin = defaultBottomMargin + overlapLogical;
                    
                    // Cap it so it doesn't fly away (e.g. max window height - buffer)
                    if (newBottomMargin > Bounds.Height - 50) newBottomMargin = Bounds.Height - 50;

                    vm.ToolbarMargin = new Avalonia.Thickness(0, 0, 0, newBottomMargin);
                }
                else
                {
                    // Reset to default if fully on screen
                    if (vm.ToolbarMargin.Bottom != defaultBottomMargin)
                    {
                         vm.ToolbarMargin = new Avalonia.Thickness(0, 0, 0, defaultBottomMargin);
                    }
                }
            }
        }
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            vm.Dispose();
        }
        base.OnClosing(e);
    }
}
