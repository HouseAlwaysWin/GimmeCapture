using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using GimmeCapture.Views.Main;
using GimmeCapture.Views.Shared;
using GimmeCapture.Models;
using System;
using System.Linq;
using Avalonia.Platform;
using Avalonia.Input.Raw;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.Services.Interop;

using ReactiveUI;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    private Point _startPoint;
    private SnipWindowViewModel? _viewModel;
    private Annotation? _currentAnnotation;
    private RecordingProgressWindow? _progressWindow;
    
    // Resize State
    private bool _isResizing;
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint;
    
    // Window region for transparent hole (mouse pass-through)
    private IDisposable? _selectionRectSubscription;
    private Rect _originalRect;
    
    // Services
    private readonly ClipboardService _clipboardService = new ClipboardService();

    private enum ResizeDirection
    {
        None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    public SnipWindow()
    {
        InitializeComponent();
        
        // Listen to pointer events on the window or canvas
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        
        // Close on Escape
        KeyDown += OnKeyDown;
    }
    
    private System.Collections.Generic.List<Window> _hiddenTopmostWindows = new();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Position logic ...
        
        // Defer Z-Order logic to ensure window is fully initialized
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            Console.WriteLine("[SnipWindow] OnOpened Post callback executing");
            
            if (_viewModel != null)
            {
                Console.WriteLine("[SnipWindow] ViewModel is not null, setting properties");
                _viewModel.VisualScaling = this.RenderScaling;
                _viewModel.ScreenOffset = this.Position;
                _viewModel.RefreshWindowRects(this.TryGetPlatformHandle()?.Handle);

                // Populate AllScreenBounds for multi-monitor UI
                double scaling = this.RenderScaling;
                var allScreens = this.Screens.All;
                Console.WriteLine($"[SnipWindow] Detected {allScreens.Count} screens for multi-monitor UI.");
                var screenBoundsList = new System.Collections.Generic.List<ScreenBoundsViewModel>();
                foreach (var s in allScreens)
                {
                    Console.WriteLine($"[SnipWindow] Screen: {s.Bounds}, Scaling: {s.Scaling}");
                    screenBoundsList.Add(new ScreenBoundsViewModel
                    {
                        X = (s.Bounds.X - this.Position.X) / scaling,
                        Y = (s.Bounds.Y - this.Position.Y) / scaling,
                        W = s.Bounds.Width / scaling,
                        H = s.Bounds.Height / scaling
                    });
                }
                _viewModel.AllScreenBounds = new System.Collections.ObjectModel.ObservableCollection<ScreenBoundsViewModel>(screenBoundsList);
                Console.WriteLine($"[SnipWindow] AllScreenBounds populated with {_viewModel.AllScreenBounds.Count} items.");
                
                // Initial Active Screen Update
                if (GetCursorPos(out POINT p))
                {
                     var clientPoint = this.PointToClient(new PixelPoint(p.X, p.Y));
                     UpdateActiveScreenBounds(clientPoint);
                }
                
                // Trigger AI Auto-Scan (single entry point after AllScreenBounds is ready)
                if (_viewModel.ShowAIScanBox && _viewModel.CurrentState == SnipState.Detecting)
                {
                    Console.WriteLine("[SnipWindow] Triggering AI Scan after AllScreenBounds ready");
                    try
                    {
                        await _viewModel.AIScanCommand.Execute();
                        Console.WriteLine("[SnipWindow] AIScanCommand completed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SnipWindow] AI Scan exception: {ex.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine("[SnipWindow] WARNING: _viewModel is null in OnOpened!");
            }

            // Ensure SnipWindow is absolutely on top of everything
            this.Topmost = true;
            this.Activate(); 
            this.Focus();

            // Temporarily lower existing Pin windows
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win is FloatingImageWindow floating && floating.Topmost && floating.IsVisible)
                    {
                        floating.Topmost = false;
                        _hiddenTopmostWindows.Add(floating);
                    }
                }
            }
            
            // Re-assert Topmost for self just in case
            this.Topmost = false;
            this.Topmost = true;
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        
        // Cleanup subscriptions
        _selectionRectSubscription?.Dispose();
        
        // Release ViewModel resources
        _viewModel?.Dispose();
        
        // Clear window region before closing
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            Win32Helpers.ClearWindowRegion(hwnd);
        }
        
        // NEW: Ensure recording is stopped if window is closed (e.g., via ESC or system close)
        if (_viewModel != null && _viewModel.RecState != RecordingState.Idle)
        {
            // Use Fire and Forget for the command, it handles internal state
            _viewModel.StopRecordingCommand.Execute().Subscribe();
        }

        // Restore Pin windows to Topmost
        foreach (var win in _hiddenTopmostWindows)
        {
            try 
            {
                if (win.IsVisible) // Ensure not closed
                    win.Topmost = true;
            }
            catch { /* Ignore if window closed */ }
        }
        _hiddenTopmostWindows.Clear();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel = DataContext as SnipWindowViewModel;
        if (_viewModel != null)
        {
            this.GetObservable(Visual.BoundsProperty).Subscribe(b => _viewModel.ViewportSize = b.Size);
            
            // Sync Toolbar size to VM for adaptive positioning
            this.Toolbar.GetObservable(Visual.BoundsProperty).Subscribe(b =>
            {
                _viewModel.ToolbarWidth = b.Width;
                _viewModel.ToolbarHeight = b.Height;
            });

            _viewModel.IsMagnifierEnabled = true;
            _viewModel.CloseAction = () => 
            {
                Close();
            };
            
            _viewModel.HideAction = () => Hide();
            _viewModel.ShowAction = () => Show();

            _viewModel.OpenRecordingProgressWindowAction = () =>
            {
                if (_progressWindow != null) return;
                
                _progressWindow = new RecordingProgressWindow
                {
                    DataContext = _viewModel,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _progressWindow.Show();
                Hide(); // Hide main window to allow user interaction
            };

            _viewModel.CloseRecordingProgressWindowAction = () =>
            {
                if (_progressWindow != null)
                {
                    _progressWindow.Close();
                    _progressWindow = null;
                }
                
                // Show main window back after finalization (e.g. for file picker)
                // Unless it was already closed/closing
                if (this.IsVisible)
                {
                    Show();
                }
            };
            
            // Subscribe to SelectionRect, CurrentState, and IsDrawingMode changes to update window region
            // Added Throttle as per user request to prevent UI flickering during heavy updates
            _selectionRectSubscription = _viewModel.WhenAnyValue(
                x => x.SelectionRect, 
                x => x.CurrentState, 
                x => x.IsDrawingMode,
                x => x.ToolbarWidth,
                x => x.ToolbarHeight)
                .Throttle(TimeSpan.FromMilliseconds(16)) // ~60fps Limit
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(tuple => UpdateWindowRegion(tuple.Item1, tuple.Item2, tuple.Item3));
            
            _viewModel.FocusWindowAction = () =>
            {
                this.Focus();
            };

            _viewModel.PickSaveFileAction = async () =>
            {
                 var topLevel = TopLevel.GetTopLevel(this);
                 if (topLevel == null) return null;
                 
                 bool isRecording = _viewModel.IsRecordingMode;
                 string defaultExt = isRecording ? _viewModel.RecordFormat : "png";
                 string fileTypeName = isRecording ? $"{defaultExt.ToUpper()} Video" : "PNG Image";
                 string pattern = $"*.{defaultExt}";
                 
                 var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                 {
                     Title = isRecording ? "Save Recording" : "Save Screenshot",
                     DefaultExtension = defaultExt,
                     ShowOverwritePrompt = true,
                     SuggestedFileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}",
                     FileTypeChoices = new[]
                     {
                         new Avalonia.Platform.Storage.FilePickerFileType(fileTypeName) { Patterns = new[] { pattern } }
                     }
                 });
                 
                 return file?.Path.LocalPath;
            };

            _viewModel.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI) =>
            {
                // Use settings directly from MainVm to ensure consistency
                bool hideDecoration = _viewModel.MainVm?.HideSnipPinDecoration ?? false;
                bool hideBorder = _viewModel.MainVm?.HideSnipPinBorder ?? false;
                var aiService = _viewModel.MainVm?.AIResourceService;
                
                if (aiService == null)
                {
                     // Fallback check (shouldn't happen if MainVm is set)
                     System.Diagnostics.Debug.WriteLine("AIResourceService is null!");
                     return;
                }
                
                if (_viewModel.MainVm == null) return;
                var vm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, hideDecoration, hideBorder, _clipboardService, aiService, _viewModel.MainVm.AppSettingsService);
                vm.WingScale = _viewModel.WingScale;
                vm.CornerIconScale = _viewModel.CornerIconScale;
                
                try
                {
                    // Calculate Window Size & Position based on the padding needed for decorations
                    // The 'rect' is the IMAGE position/size in Logical pixels.
                    // Window Position must be in PHYSICAL pixels.
                    double scaling = _viewModel.VisualScaling;
                    var padding = vm.WindowPadding;
                    
                    // Convert Logical Rect to Physical Screen coordinates
                    int physicalX = (int)(rect.X * scaling) + _viewModel.ScreenOffset.X;
                    int physicalY = (int)(rect.Y * scaling) + _viewModel.ScreenOffset.Y;
                    
                    // Convert Logical Padding to Physical
                    int physicalPaddingLeft = (int)(padding.Left * scaling);
                    int physicalPaddingTop = (int)(padding.Top * scaling);
                    
                    // Create Window
                    var win = new FloatingImageWindow
                    {
                        DataContext = vm,
                        // Set physical position using converted values
                        Position = new PixelPoint(physicalX - physicalPaddingLeft, physicalY - physicalPaddingTop),
                        // Width/Height in Avalonia are Logical
                        Width = rect.Width + padding.Left + padding.Right,
                        Height = rect.Height + padding.Top + padding.Bottom
                    };

                    // Save Action
                    vm.SaveAction = async () =>
                    {
                        try
                        {
                            var topLevel = TopLevel.GetTopLevel(win);
                            if (topLevel?.StorageProvider is { } storageProvider)
                            {
                                var file = await storageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                                {
                                    Title = "Save Pinned Image",
                                    DefaultExtension = "png",
                                    ShowOverwritePrompt = true,
                                    SuggestedFileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}",
                                    FileTypeChoices = new[]
                                    {
                                        new Avalonia.Platform.Storage.FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                                    }
                                });

                                if (file != null)
                                {
                                    using var stream = await file.OpenWriteAsync();
                                    vm.Image?.Save(stream); // Save current image (might be transparent)
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to save pinned image: {ex}");
                        }
                    };
                    
                    win.Show();
                    
                    // Auto-Run AI if requested
                    if (runAI)
                    {
                        // Use dispatcher to ensure window is shown/initialized before starting
                         Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                            vm.RemoveBackgroundCommand.Execute().Subscribe();
                         });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing Floating Window: {ex}");
                }
            };
        }
    }

    // Text Dragging State
    private bool _isDraggingAnnotation;
    private Annotation? _draggingAnnotation;
    private Point _dragOffset;
    
    // Selection Moving State
    private bool _isMovingSelection;
    private Point _moveStartPoint;

    // Flag to Debounce Text Entry Finish
    private DateTime _lastTextFinishTime = DateTime.MinValue;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_viewModel == null) return;

            // If currently entering text, cancel it
            if (_viewModel.IsEnteringText)
            {
                _viewModel.CancelTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
                return;
            }

            // Prevent resetting or closing if we are actively recording
            if (_viewModel.RecState != RecordingState.Idle)
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.IsDrawingMode)
            {
                _viewModel.IsDrawingMode = false;
                e.Handled = true;
            }
            else if (_viewModel.CurrentState == SnipState.Selecting || 
                     _viewModel.CurrentState == SnipState.Selected)
            {
                // Reset to Detecting to re-enable auto-detection (red box)
                _viewModel.CurrentState = SnipState.Detecting;
                _viewModel.SelectionRect = new Rect(0,0,0,0);
                e.Handled = true;
            }
            else
            {
                 Close();
            }
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_viewModel != null)
            {
                if (_viewModel.IsRecordingMode)
                {
                    _viewModel.StopRecordingCommand.Execute().Subscribe();
                }
                else
                {
                    _viewModel.SaveCommand.Execute().Subscribe();
                }
            }
        }
    }
    
    private void UpdateActiveScreenBounds(Point point)
    {
        if (_viewModel == null) return;
        
        // Point is in Window Logical Coordinates (if from PointerMoved args) or Screen Physical (if from desktop).
        // Let's assume input 'point' is Screen Physical for consistency with ScreenFromPoint, 
        // OR we use the Window-relative point and `PointToScreen`.
        
        // Easier: Use `Screens.ScreenFromPointer(this)`? No, window spans multiple.
        // Use `Screens.ScreenFromPoint(this.PointToScreen(point))`?
        
        // Let's get the absolute physical point from the event or cursor.
        var pixelPoint = new PixelPoint((int)point.X, (int)point.Y); // Rough conversion if point is logical? No.
        
        // Actually, let's just use the mouse position from the event which is relative to window.
        // Convert to Screen Physical
        var screenPoint = this.PointToScreen(point);
        
        var screen = Screens.ScreenFromPoint(screenPoint);
        if (screen != null)
        {
             // Calculate Bounds relative to the Window's TopLeft (which is MinX, MinY of virtual desktop)
             // Window Position: this.Position
             // Screen Bounds: screen.Bounds
             
             double scaling = _viewModel.VisualScaling; // Should be 1.0 if we set it right, or whatever the window scaling is.
             // Wait, `VisualScaling` in VM is set from `this.RenderScaling`.
             
             // Relative Position = (ScreenX - WindowX) / Scaling
             double relX = (screen.Bounds.X - this.Position.X) / scaling;
             double relY = (screen.Bounds.Y - this.Position.Y) / scaling;
             double relW = screen.Bounds.Width / scaling;
             double relH = screen.Bounds.Height / scaling;
             
             _viewModel.ActiveScreenBounds = new Rect(relX, relY, relW, relH);
        }
    }

    private ResizeDirection GetDirectionFromName(string? name)
    {
        return name switch
        {
            "HandleTopLeft" => ResizeDirection.TopLeft,
            "HandleTopRight" => ResizeDirection.TopRight,
            "HandleBottomLeft" => ResizeDirection.BottomLeft,
            "HandleBottomRight" => ResizeDirection.BottomRight,
            "HandleTop" => ResizeDirection.Top,
            "HandleBottom" => ResizeDirection.Bottom,
            "HandleLeft" => ResizeDirection.Left,
            "HandleRight" => ResizeDirection.Right,
            _ => ResizeDirection.None
        };
    }
}
