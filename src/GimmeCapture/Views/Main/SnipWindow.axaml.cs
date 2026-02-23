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
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.Services.Interop;

using ReactiveUI;
using Avalonia.Interactivity;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    private Point _startPoint;
    private SnipWindowViewModel? _viewModel;
    private Annotation? _currentAnnotation;
    private RecordingProgressWindow? _progressWindow;
    
    // Pointer Interaction State (replaces multiple _is* booleans)
    private PointerInteractionState _pointerState = PointerInteractionState.None;
    
    // Resize State
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint;
    
    // Window region for transparent hole (mouse pass-through)
    private IDisposable? _selectionRectSubscription;
    private Rect _originalRect;
    
    // Services
    private readonly ClipboardService _clipboardService = new ClipboardService();
    private readonly HotkeyRouterService _hotkeyRouter = new();
    private readonly IScreenLayoutService _screenLayoutService;
    private readonly IWindowLayerService _windowLayerService;

    private enum PointerInteractionState
    {
        None,
        ResizingSelection,
        MovingSelection,
        DraggingAnnotation,
        DraggingTranslationResult,
        TranslationSelecting,
        DraggingToolbar,
        MovingTranslationBox,
        ResizingTranslationBox
    }

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

    public SnipWindow() : this(new AvaloniaScreenLayoutService(), new AvaloniaWindowLayerService())
    {
    }

    public SnipWindow(IScreenLayoutService screenLayoutService, IWindowLayerService windowLayerService)
    {
        _screenLayoutService = screenLayoutService ?? throw new ArgumentNullException(nameof(screenLayoutService));
        _windowLayerService = windowLayerService ?? throw new ArgumentNullException(nameof(windowLayerService));

        InitializeComponent();
        
        // Listen to pointer events on the window or canvas
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        
        // Close on Escape
        KeyDown += OnKeyDown;
    }
    
    private IReadOnlyList<Window> _hiddenTopmostWindows = [];

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
                if (!_viewModel.IsTranslationMode)
                {
                    _viewModel.RefreshWindowRects(this.TryGetPlatformHandle()?.Handle);
                }

                // Initialize Win32 Hook for click-through
                InitializeWin32Hook();

                // Populate AllScreenBounds for multi-monitor UI
                double scaling = this.RenderScaling;
                var allScreens = this.Screens.All;
                Console.WriteLine($"[SnipWindow] Detected {allScreens.Count} screens for multi-monitor UI.");
                var physicalScreenBounds = allScreens.Select(s => s.Bounds).ToList();
                var relativeScreenBounds = _screenLayoutService.BuildRelativeScreenBounds(physicalScreenBounds, this.Position, scaling);
                var screenBoundsList = new System.Collections.Generic.List<ScreenBoundsViewModel>(relativeScreenBounds.Count);
                foreach (var bounds in relativeScreenBounds)
                {
                    screenBoundsList.Add(new ScreenBoundsViewModel
                    {
                        X = bounds.X,
                        Y = bounds.Y,
                        W = bounds.Width,
                        H = bounds.Height
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
                // 翻譯模式不使用 SAM2 掃描
                if (_viewModel.ShowAIScanBox && !_viewModel.IsTranslationMode && _viewModel.CurrentState == SnipState.Detecting)
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
                
                // 翻譯模式：在 ViewportSize 和 AllScreenBounds 就緒後重新初始化工具列位置
                if (_viewModel.IsTranslationMode)
                {
                    _viewModel.InitializeTranslationToolbarPosition();
                    Console.WriteLine($"[SnipWindow] Translation toolbar at ({_viewModel.ToolbarLeft}, {_viewModel.ToolbarTop})");
                    
                    // NEW: Exclude from capture specifically for Translation Mode to prevent flickering during background OCR updates.
                    var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, false);
                        Console.WriteLine("[SnipWindow] Applied WDA_EXCLUDEFROMCAPTURE for Translation Mode.");
                    }
                }
            }
            else
            {
                Console.WriteLine("[SnipWindow] WARNING: _viewModel is null in OnOpened!");
            }

            this.Activate(); 
            this.Focus();
            
            // Z-Order nudge: Some popups (like ComboBox dropdowns) might be stubborn.
            // Toggling Topmost and re-activating after a short delay helps.
            _ = Task.Run(async () => 
            {
                await Task.Delay(500);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    this.Topmost = false;
                    this.Topmost = true;
                    this.Activate();
                    this.Focus();
                    Console.WriteLine("[SnipWindow] Z-Order nudge applied.");
                });
            });

            // Temporarily lower existing Pin windows
            _hiddenTopmostWindows = _windowLayerService.LowerTopmostWindowsOfType<FloatingImageWindow>();

            // Track Focus to fix Ctrl+C conflict with SelectableTextBlock
            this.AddHandler(InputElement.GotFocusEvent, (s, ev) => 
            {
                if (_viewModel == null) return;
                bool isTextControl = ev.Source is SelectableTextBlock || ev.Source is TextBox;
                _viewModel.IsInputFocused = isTextControl;
            }, RoutingStrategies.Bubble);

            this.AddHandler(InputElement.LostFocusEvent, (s, ev) => 
            {
                if (_viewModel == null) return;
                _viewModel.IsInputFocused = false;
            }, RoutingStrategies.Bubble);
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
        _windowLayerService.RestoreTopmostWindows(_hiddenTopmostWindows);
        _hiddenTopmostWindows = [];
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

            // Toggle window capture visibility based on recording state
            // When recording, exclude SnipWindow from capture so toolbar/decorations don't show in video
            if (_viewModel.RecordingService != null)
            {
                _viewModel.RecordingService.WhenAnyValue(x => x.State)
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(state => 
                    {
                        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                        {
                            // Recording mode: keep overlay visible to user, but exclude window from capture source.
                            if (state != RecordingState.Idle && _viewModel.MainVm?.HideRecordSelectionBorder == true)
                            {
                                Win32Helpers.SetWindowCaptureVisibility(hwnd, false);
                            }
                            else if (!_viewModel.IsTranslationMode)
                            {
                                // Translation mode manages capture visibility separately in OnOpened.
                                Win32Helpers.SetWindowCaptureVisibility(hwnd, true);
                            }
                        }
                        
                        // Trigger UI update for decorations
                        _viewModel.RaisePropertyChanged(nameof(_viewModel.HideSelectionDecoration));
                        _viewModel.RaisePropertyChanged(nameof(_viewModel.HideFrameBorder));
                        _viewModel.RaisePropertyChanged(nameof(_viewModel.IsToolbarVisible));
                    });
            }

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
            
            // Subscribe to Geometry and state changes to update window region
            // Subscribe to Geometry and state changes to update window region
            // Split into two subscriptions if arguments exceed 7 to avoid compilation error
            var trigger1 = _viewModel.WhenAnyValue(
                x => x.MaskGeometry,
                x => x.SelectionRect, 
                x => x.CurrentState, 
                x => x.IsDrawingMode,
                x => x.IsTranslationMode,
                x => x.IsTranslationSelectionActive,
                x => x.RecState);
                
            var trigger2 = _viewModel.WhenAnyValue(
                x => x.ToolbarWidth,
                x => x.ToolbarHeight);

            _selectionRectSubscription = System.Reactive.Linq.Observable.CombineLatest(trigger1, trigger2, (t1, t2) => t1)
                .Throttle(TimeSpan.FromMilliseconds(16))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(tuple => UpdateWindowRegion(tuple.Item2, tuple.Item3, tuple.Item4));
            
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

            _viewModel.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI, initialInteractive) =>
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
                var vm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, hideDecoration, hideBorder, _clipboardService, aiService, _viewModel.MainVm.AppSettingsService, _viewModel.MainVm.AIPathService);
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
                    
                    // Auto-Run AI if requested
                    if (runAI)
                    {
                        // Use dispatcher to ensure window is shown/initialized before starting
                         Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                            vm.RemoveBackgroundCommand.Execute().Subscribe();
                         });
                    }

                    // Initial Interactive mode if requested
                    if (initialInteractive)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                            vm.IsPointRemovalMode = true;
                        });
                    }

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
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error showing Floating Window: {ex}");
                }
            };
        }
    }

    // Text Dragging State
    private Annotation? _draggingAnnotation;
    private Point _dragOffset;
    
    // Selection Moving State
    private Point _moveStartPoint;

    // Flag to Debounce Text Entry Finish
    private DateTime _lastTextFinishTime = DateTime.MinValue;

    private void RootGrid_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel == null) return;

        var src = e.Source as Control;
        
        // Let SelectableTextBlock show its own ContextMenu
        if (src is SelectableTextBlock || src?.FindAncestorOfType<SelectableTextBlock>() != null)
        {
            return;
        }

        // Translation mode: Right-click deletes box, NO context menu on the background
        if (_viewModel.IsTranslationMode)
        {
            e.Handled = true;
            return;
        }

        // Selection mode: Right-click cancels box, NO context menu when interacting with box
        if (_viewModel.CurrentState == SnipState.Selecting || _viewModel.CurrentState == SnipState.Selected)
        {
            e.Handled = true;
            return;
        }
    }

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

        // Manual Hotkey Routing (Bypassing XAML KeyBinding quirks)
        if (_viewModel != null && !e.Handled)
        {
            System.Diagnostics.Debug.WriteLine($"[SnipWindow.axaml.cs] OnKeyDown: Key={e.Key}, Mods={e.KeyModifiers}, ActiveAction={_viewModel.ActiveActionHotkey}, IsInputFocused={_viewModel.IsInputFocused}");

            bool IsMatch(string hotkey)
            {
                try { return KeyGesture.Parse(hotkey).Matches(e); }
                catch { return false; }
            }

            var action = _hotkeyRouter.ResolveWindowHotkeyAction(
                _viewModel.ActiveActionHotkey,
                _viewModel.ActiveToolbarHotkey,
                _viewModel.SaveHotkey,
                _viewModel.CopyHotkey,
                IsMatch);

            switch (action)
            {
                case HotkeyRouterService.WindowHotkeyAction.ActiveAction:
                    System.Diagnostics.Debug.WriteLine("[SnipWindow.axaml.cs] Matched ActiveActionHotkey! Firing HandleActiveActionHotkeyCommand.");
                    _viewModel.HandleActiveActionHotkeyCommand?.Execute().Subscribe();
                    e.Handled = true;
                    break;
                case HotkeyRouterService.WindowHotkeyAction.ToggleToolbar:
                    System.Diagnostics.Debug.WriteLine("[SnipWindow.axaml.cs] Matched ActiveToolbarHotkey! Firing ToggleToolbarCommand.");
                    _viewModel.ToggleToolbarCommand?.Execute().Subscribe();
                    e.Handled = true;
                    break;
                case HotkeyRouterService.WindowHotkeyAction.Save:
                    System.Diagnostics.Debug.WriteLine("[SnipWindow.axaml.cs] Matched SaveHotkey! Firing SaveCommand.");
                    _viewModel.SaveCommand?.Execute().Subscribe();
                    e.Handled = true;
                    break;
                case HotkeyRouterService.WindowHotkeyAction.Copy:
                    System.Diagnostics.Debug.WriteLine("[SnipWindow.axaml.cs] Matched CopyHotkey! Firing CopyCommand.");
                    _viewModel.CopyCommand?.Execute().Subscribe();
                    e.Handled = true;
                    break;
            }
        }
    }
    
    private void UpdateActiveScreenBounds(Point point)
    {
        if (_viewModel == null) return;

        var bounds = _screenLayoutService.TryGetActiveScreenBounds(this.Screens, this, point, _viewModel.VisualScaling);
        if (bounds.HasValue)
        {
             _viewModel.ActiveScreenBounds = bounds.Value;
        }
    }

    // Translation Dragging State
    private Point _translationDragOffset;

    private void Translation_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel == null || sender is not Border border) return;
        
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _pointerState = PointerInteractionState.DraggingTranslationResult;
            _translationDragOffset = e.GetPosition(this);
            _translationDragOffset = new Point(
                _translationDragOffset.X - _viewModel.TranslationOverlayLeft,
                _translationDragOffset.Y - _viewModel.TranslationOverlayTop);
            
            e.Pointer.Capture(border);
            e.Handled = true;
        }
    }

    private void Translation_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointerState != PointerInteractionState.DraggingTranslationResult || _viewModel == null) return;

        var currentPos = e.GetPosition(this);
        _viewModel.TranslationOverlayLeft = currentPos.X - _translationDragOffset.X;
        _viewModel.TranslationOverlayTop = currentPos.Y - _translationDragOffset.Y;
        _viewModel.IsTranslationOverlayManuallyPositioned = true;
        
        e.Handled = true;
    }

    private void Translation_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointerState == PointerInteractionState.DraggingTranslationResult && sender is Border border)
        {
            _pointerState = PointerInteractionState.None;
            e.Pointer.Capture(null);
            e.Handled = true;
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
