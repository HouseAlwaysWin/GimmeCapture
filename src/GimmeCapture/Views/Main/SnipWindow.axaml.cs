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
using Avalonia.Platform;
using Avalonia.Input.Raw;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Interop;

using ReactiveUI;
using Avalonia.Interactivity;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace GimmeCapture.Views.Main;

public partial class SnipWindow : Window
{
    private Point _startPoint;
    private SnipWindowViewModel? _viewModel;
    private Annotation? _currentAnnotation;
    private RecordingProgressWindow? _progressWindow;
    private bool _activeActionHotkeyHeld;
    
    // Pointer Interaction State (replaces multiple _is* booleans)
    private PointerInteractionState _pointerState = PointerInteractionState.None;
    
    // Resize State
    private ResizeDirection _resizeDirection;
    private Point _resizeStartPoint;
    
    // Window region for transparent hole (mouse pass-through)
    private IDisposable? _selectionRectSubscription;
    private IDisposable? _viewportBoundsSubscription;
    private IDisposable? _toolbarBoundsSubscription;
    private IDisposable? _recordingStateSubscription;
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

    public SnipWindow() : this(new NoOpScreenLayoutService(), new NoOpWindowLayerService())
    {
    }

    public SnipWindow(IScreenLayoutService screenLayoutService, IWindowLayerService windowLayerService)
    {
        _screenLayoutService = screenLayoutService ?? throw new ArgumentNullException(nameof(screenLayoutService));
        _windowLayerService = windowLayerService ?? throw new ArgumentNullException(nameof(windowLayerService));

        InitializeComponent();
        
        // Listen through the tunnel route so text controls inside translation boxes
        // cannot swallow drag/resize gestures before the window sees them.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        
        // Close on Escape
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
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
                // Initialize Win32 Hook for click-through
                InitializeWin32Hook();

                // Populate AllScreenBounds for multi-monitor UI
                double scaling = this.RenderScaling;
                var allScreens = this.Screens.All;
                Console.WriteLine($"[SnipWindow] Detected {allScreens.Count} screens for multi-monitor UI.");
                var physicalScreenBounds = allScreens.AsValueEnumerable().Select(s => s.Bounds).ToList();
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
                
                // 翻譯模式：在 ViewportSize 和 AllScreenBounds 就緒後重新初始化工具列位置
                if (_viewModel.IsTranslationMode)
                {
                    _viewModel.InitializeTranslationToolbarPosition();
                    // Re-center after SnipToolbar finishes layout (MaxWidth / WrapPanel need a real measure pass).
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var measuredWidth = Toolbar.Bounds.Width;
                        if (measuredWidth > 1)
                        {
                            _viewModel.ToolbarWidth = measuredWidth;
                        }
                        _viewModel.InitializeTranslationToolbarPosition();
                    }, Avalonia.Threading.DispatcherPriority.Loaded);
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

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel == null || _viewModel.IsTranslationMode)
                {
                    return;
                }

                try
                {
                    _viewModel.RefreshWindowRects(this.TryGetPlatformHandle()?.Handle);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SnipWindow] RefreshWindowRects exception: {ex.Message}");
                }
            }, Avalonia.Threading.DispatcherPriority.Background);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel == null
                    || !_viewModel.ShowAIScanBox
                    || _viewModel.IsTranslationMode
                    || _viewModel.CurrentState != SnipState.Detecting)
                {
                    return;
                }

                Console.WriteLine("[SnipWindow] Triggering deferred AI Scan after initial overlay display");
                try
                {
                    _viewModel.AIScanCommand?.Execute().Subscribe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SnipWindow] Deferred AI Scan exception: {ex.Message}");
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
            
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

            // Track keyboard focus so global/LL hook shortcuts do not steal keys while typing in translation UI, ComboBoxes, etc.
            // Update synchronously so the first keystroke after focus is not routed as a hotkey (Post deferred too long).
            void OnTreeKeyboardFocusChanged(object? _, RoutedEventArgs __)
            {
                RefreshKeyboardInteractionFocusFlag();
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    RefreshKeyboardInteractionFocusFlag,
                    Avalonia.Threading.DispatcherPriority.Input);
            }

            this.AddHandler(InputElement.GotFocusEvent, OnTreeKeyboardFocusChanged, RoutingStrategies.Bubble);
            this.AddHandler(InputElement.LostFocusEvent, OnTreeKeyboardFocusChanged, RoutingStrategies.Bubble);
            RefreshKeyboardInteractionFocusFlag();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void RefreshKeyboardInteractionFocusFlag()
    {
        if (_viewModel == null) return;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _viewModel.IsInputFocused = IsKeyboardInteractionFocus(focused);
    }

    private static bool IsKeyboardInteractionFocus(IInputElement? el)
    {
        if (el == null) return false;
        // ComboBox excluded: translation toolbar language combos use letter keys for search (e.g. T → "Traditional Chinese"),
        // which steals Translate-all hotkey "T" and blocks manual routing in OnKeyDown. Users pick languages with mouse/arrows.
        if (el is TextBox or SelectableTextBlock or AutoCompleteBox) return true;
        if (el is Control c)
        {
            return c.FindAncestorOfType<TextBox>() != null
                   || c.FindAncestorOfType<SelectableTextBlock>() != null
                   || c.FindAncestorOfType<AutoCompleteBox>() != null;
        }

        return false;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        
        // Cleanup subscriptions
        _selectionRectSubscription?.Dispose();
        _selectionRectSubscription = null;
        _viewportBoundsSubscription?.Dispose();
        _viewportBoundsSubscription = null;
        _toolbarBoundsSubscription?.Dispose();
        _toolbarBoundsSubscription = null;
        _recordingStateSubscription?.Dispose();
        _recordingStateSubscription = null;
        
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
        _selectionRectSubscription?.Dispose();
        _selectionRectSubscription = null;
        _viewportBoundsSubscription?.Dispose();
        _viewportBoundsSubscription = null;
        _toolbarBoundsSubscription?.Dispose();
        _toolbarBoundsSubscription = null;
        _recordingStateSubscription?.Dispose();
        _recordingStateSubscription = null;

        _viewModel = DataContext as SnipWindowViewModel;
        if (_viewModel != null)
        {
            var vm = _viewModel;
            _viewportBoundsSubscription = this.GetObservable(Visual.BoundsProperty)
                .Subscribe(b => vm.ViewportSize = b.Size);
            
            // Sync Toolbar size to VM for adaptive positioning
            _toolbarBoundsSubscription = this.Toolbar.GetObservable(Visual.BoundsProperty).Subscribe(b =>
            {
                vm.ToolbarWidth = b.Width;
                vm.ToolbarHeight = b.Height;
            });

            // WDA_EXCLUDEFROMCAPTURE: translation OCR / recording without annotations exclude SnipWindow from
            // FFmpeg gdigrab so output matches full SelectionRect without chrome; annotations require capturable window + inset crop (see RecordingUsesWindowsExcludeFromCapture).
            vm.SyncRecordingScreenCaptureAffinity = () => ApplyRecordingScreenCaptureAffinity(vm);
            if (vm.RecordingService != null)
            {
                _recordingStateSubscription = Observable.Merge(
                        vm.RecordingService.WhenAnyValue(x => x.State).Select(_ => 0),
                        vm.WhenAnyValue(x => x.RecordingUsesWindowsExcludeFromCapture).Select(_ => 0),
                        vm.WhenAnyValue(x => x.IsTranslationMode).Select(_ => 0))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ =>
                    {
                        ApplyRecordingScreenCaptureAffinity(vm);
                        vm.RaisePropertyChanged(nameof(vm.HideSelectionDecoration));
                        vm.RaisePropertyChanged(nameof(vm.HideFrameBorder));
                        vm.RaisePropertyChanged(nameof(vm.IsToolbarVisible));
                        vm.RaisePropertyChanged(nameof(vm.IsToolbarShownOnScreen));
                    });
            }

            ApplyRecordingScreenCaptureAffinity(vm);

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
            var trigger1 = vm.WhenAnyValue(
                x => x.MaskGeometry,
                x => x.SelectionRect, 
                x => x.CurrentState, 
                x => x.IsDrawingMode,
                x => x.IsTranslationMode,
                x => x.IsTranslationSelectionActive,
                x => x.RecState);
                
            var trigger2 = vm.WhenAnyValue(
                x => x.ToolbarWidth,
                x => x.ToolbarHeight,
                x => x.ShowToolbar,
                x => x.IsToolbarVisible,
                x => x.IsToolbarShownOnScreen,
                x => x.CurrentTranslationTool);

            // Recompute Win32 region when translation boxes are added/removed (not covered by SelectionRect alone).
            var userSelectionsChanged = Observable
                .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => vm.UserSelections.CollectionChanged += h,
                    h => vm.UserSelections.CollectionChanged -= h)
                .Select(_ => 0)
                .StartWith(0);

            var translatedBlocksChanged = Observable
                .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => vm.TranslatedBlocks.CollectionChanged += h,
                    h => vm.TranslatedBlocks.CollectionChanged -= h)
                .Select(_ => 0)
                .StartWith(0);

            var translationOverlayChanged = vm.WhenAnyValue(
                    x => x.TranslationOverlayLeft,
                    x => x.TranslationOverlayTop)
                .Select(_ => 0)
                .StartWith(0);

            _selectionRectSubscription = Observable.CombineLatest(
                    trigger1,
                    trigger2,
                    userSelectionsChanged,
                    translatedBlocksChanged,
                    translationOverlayChanged,
                    (t1, t2, _, __, ___) => t1)
                .Throttle(TimeSpan.FromMilliseconds(16))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(tuple => UpdateWindowRegion(tuple.Item2, tuple.Item3, tuple.Item4));
            
            _viewModel.FocusWindowAction = () =>
            {
                this.Focus();
            };

            _viewModel.PersistTranslationSelectionsAction = PersistTranslatedSelectionsToDetachedLayer;

            _viewModel.CaptureDrawingModeSnapshotAsync = async () =>
            {
                var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                bool restoreCaptureVisibility = false;

                try
                {
                    if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: false);
                        restoreCaptureVisibility = true;
                        await Task.Delay(50);
                    }

                    return await vm.CaptureRegionBitmapAsync();
                }
                finally
                {
                    if (restoreCaptureVisibility && hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: true);
                    }
                }
            };

            _viewModel.PickSaveFileAction = async () =>
            {
                 var topLevel = TopLevel.GetTopLevel(this);
                 if (topLevel == null) return null;
                 
                 bool isRecording = _viewModel.IsRecordingMode;
                 string defaultExt = isRecording ? _viewModel.RecordFormat : "png";
                 string fileTypeName = isRecording ? $"{defaultExt.ToUpper()} Video" : "PNG Image";
                 string pattern = $"*.{defaultExt}";
                 
                 var fileChoices = new System.Collections.Generic.List<Avalonia.Platform.Storage.FilePickerFileType>();
                 if (isRecording)
                 {
                     fileChoices.Add(new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.gif", "*.webm", "*.mov" } });
                     fileChoices.Add(new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } });
                 }
                 else
                 {
                     fileChoices.Add(new Avalonia.Platform.Storage.FilePickerFileType(fileTypeName) { Patterns = new[] { pattern } });
                 }

                 var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                 {
                     Title = isRecording ? "Save Recording" : "Save Screenshot",
                     DefaultExtension = defaultExt,
                     ShowOverwritePrompt = true,
                    SuggestedFileName = CaptureFileNameService.SuggestedBaseName(),
                     FileTypeChoices = fileChoices
                 });
                 
                 return file?.Path.LocalPath;
            };

            _viewModel.OpenPinnedVideoWindowAction = (recordingPath, pixelWidth, pixelHeight, originalWidth, originalHeight, color, thickness, hideDecoration, hideBorder) =>
            {
                var vm = new FloatingVideoViewModel(
                    recordingPath,
                    string.Empty,
                    pixelWidth,
                    pixelHeight,
                    originalWidth,
                    originalHeight,
                    color,
                    thickness,
                    hideDecoration,
                    hideBorder,
                    _clipboardService,
                    _viewModel.MainVm?.AppSettingsService);

                var padding = vm.WindowPadding;
                var window = new FloatingVideoWindow
                {
                    DataContext = vm,
                    Width = originalWidth + padding.Left + padding.Right,
                    Height = originalHeight + padding.Top + padding.Bottom,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                window.Show();
            };

            _viewModel.OpenPinWindowAction = (bitmap, rect, color, thickness, runAI, initialInteractive, pinnedText, inferredFontSize) =>
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
                var vm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, hideDecoration, hideBorder, _clipboardService, aiService, _viewModel.MainVm.SAM2RuntimeService, _viewModel.MainVm.AppSettingsService, _viewModel.MainVm.AIPathService, pinnedText, inferredFontSize);
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
                                    SuggestedFileName = CaptureFileNameService.SuggestedBaseName(),
                                    FileTypeChoices = new[]
                                    {
                                        new Avalonia.Platform.Storage.FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } }
                                    }
                                });

                                if (file != null)
                                {
                                    using var stream = await file.OpenWriteAsync();
                                    vm.Image?.Save(stream); // Save current image (might be transparent)
                                    FileLocationService.RevealInFileExplorer(file.Path.LocalPath);
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

        if (ResolveTranslationSelectionFromVisualTree(src) != null)
        {
            return;
        }

        // Suppress system context menu in all capture modes.
        // HandleRightClick already handles right-click logic (cancel selection / close).
        e.Handled = true;
    }

    private void PersistTranslatedSelectionsToDetachedLayer()
    {
        if (_viewModel == null)
        {
            return;
        }

        var items = _viewModel.MaterializePersistentTranslationSelections();
        if (items.Count == 0)
        {
            return;
        }

        TranslationResultLayerManager.ShowOrAppend(
            _viewModel.ScreenOffset,
            _viewModel.ViewportSize,
            _viewModel.VisualScaling,
            _viewModel.TranslationOcrHighlightColor,
            items,
            CopyPersistentTranslationAsync,
            item => _viewModel.PinPersistentTranslationItemAsync(item));

        _viewModel.UpdateMask();
    }

    private async Task CopyPersistentTranslationAsync(object? item)
    {
        var text = item switch
        {
            TranslationResultItem result => !string.IsNullOrWhiteSpace(result.TranslatedText)
                ? result.TranslatedText
                : result.OriginalText,
            string rawText => rawText,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _clipboardService.CopyTextAsync(text);
        _viewModel?.MainVm?.SetStatus("StatusCopied");
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

            if (_viewModel.IsTranslationMode)
            {
                Close();
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

        // Prefer live focus (same-frame as key) — VM flag can lag one dispatcher tick behind GotFocus.
        var focusedNow = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var textOrListInputFocused = IsKeyboardInteractionFocus(focusedNow);
        if (_viewModel != null && textOrListInputFocused != _viewModel.IsInputFocused)
        {
            _viewModel.IsInputFocused = textOrListInputFocused;
        }

        // Manual Hotkey Routing (Bypassing XAML KeyBinding quirks)
        if (_viewModel != null && !e.Handled && !textOrListInputFocused)
        {
            System.Diagnostics.Debug.WriteLine($"[SnipWindow.axaml.cs] OnKeyDown: Key={e.Key}, Mods={e.KeyModifiers}, ActiveAction={_viewModel.ActiveActionHotkey}, IsInputFocused={_viewModel.IsInputFocused}");

            bool IsMatch(string hotkey)
            {
                try 
                { 
                    var gesture = KeyGesture.Parse(hotkey);
                    if (gesture.Matches(e)) return true;

                    // Fallback for IME processing (e.Key might be ImeProcessed / None)
                    if ((e.Key == Key.ImeProcessed || e.Key == Key.None) && gesture.KeyModifiers == e.KeyModifiers)
                    {
                        string expectedKeyStr = gesture.Key.ToString();
                        string physicalKeyStr = e.PhysicalKey.ToString();
                        
                        // Map specific keys if necessary, e.g. Key.D1 -> PhysicalKey.Digit1
                        if (expectedKeyStr.StartsWith("D") && expectedKeyStr.Length == 2 && char.IsDigit(expectedKeyStr[1]))
                        {
                            if (physicalKeyStr == "Digit" + expectedKeyStr[1]) return true;
                        }
                        else if ((expectedKeyStr == "Return" || expectedKeyStr == "Enter") && (physicalKeyStr == "Return" || physicalKeyStr == "Enter"))
                        {
                            return true;
                        }
                        else if (string.Equals(expectedKeyStr, physicalKeyStr, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false; 
                }
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
                    if (_activeActionHotkeyHeld)
                    {
                        e.Handled = true;
                        break;
                    }
                    System.Diagnostics.Debug.WriteLine("[SnipWindow.axaml.cs] Matched ActiveActionHotkey! Firing HandleActiveActionHotkeyCommand.");
                    _activeActionHotkeyHeld = true;
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

            // Translation Mode Specific Hotkeys
            if (!e.Handled && _viewModel.IsTranslationMode)
            {
                var specificAction = _hotkeyRouter.ResolveSpecificTranslationAction(
                    _viewModel.TranslateAllHotkey,
                    IsMatch);

                if (specificAction == HotkeyRouterService.WindowHotkeyAction.TranslateAll)
                {
                    _viewModel.TranslateAllSelectionsCommand?.Execute().Subscribe();
                    e.Handled = true;
                }
                else if (IsMatch(_viewModel.TranslatePinHotkey))
                {
                    _viewModel.PinTranslationResultsCommand?.Execute().Subscribe();
                    e.Handled = true;
                }
                else if (IsMatch(_viewModel.ScanAllHotkey))
                {
                    _viewModel.ScanAllTextCommand?.Execute().Subscribe();
                    e.Handled = true;
                }
                else if (IsMatch(_viewModel.AutoDetectHotkey))
                {
                    _viewModel.ToggleAutoDetectCommand?.Execute().Subscribe();
                    e.Handled = true;
                }
                else if (IsMatch(_viewModel.ClearAllHotkey))
                {
                    _viewModel.ClearAllSelectionsCommand?.Execute().Subscribe();
                    e.Handled = true;
                }
            }
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        // Release one-shot guard so long-press key-repeat does not retrigger start/stop/pin.
        _activeActionHotkeyHeld = false;
    }

    private bool IsModifierMatch(string hotkeyLabel, KeyEventArgs e)
    {
        if (hotkeyLabel == "Shift" && (e.Key == Key.LeftShift || e.Key == Key.RightShift || e.PhysicalKey.ToString() == "ShiftLeft" || e.PhysicalKey.ToString() == "ShiftRight")) return true;
        if (hotkeyLabel == "Ctrl" && (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl || e.PhysicalKey.ToString() == "ControlLeft" || e.PhysicalKey.ToString() == "ControlRight")) return true;
        if (hotkeyLabel == "Alt" && (e.Key == Key.LeftAlt || e.Key == Key.RightAlt || e.PhysicalKey.ToString() == "AltLeft" || e.PhysicalKey.ToString() == "AltRight")) return true;
        return false;
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
        if (_viewModel == null || sender is not Control control) return;
        
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            _pointerState = PointerInteractionState.DraggingTranslationResult;
            _translationDragOffset = e.GetPosition(this);
            _translationDragOffset = new Point(
                _translationDragOffset.X - _viewModel.TranslationOverlayLeft,
                _translationDragOffset.Y - _viewModel.TranslationOverlayTop);
            
            e.Pointer.Capture(control);
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
        RequestTranslationWindowRegionRefresh();
        
        e.Handled = true;
    }

    private void Translation_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointerState == PointerInteractionState.DraggingTranslationResult)
        {
            _pointerState = PointerInteractionState.None;
            RequestTranslationWindowRegionRefresh();
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

    /// <summary>
    /// <see cref="Win32Helpers.SetWindowDisplayAffinity"/>: local display unchanged; screen capture APIs see through the window when excluded.
    /// </summary>
    private void ApplyRecordingScreenCaptureAffinity(SnipWindowViewModel vm)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;

        // Do not gate RecordingUsesWindowsExcludeFromCapture on RecState: that flag is set and Sync runs
        // before RecordingService sets State=Recording, so requiring RecState!=Idle cleared WDA and broke capture.
        bool excludeFromCapture =
            vm.IsTranslationMode
            || vm.RecordingUsesWindowsExcludeFromCapture;

        Win32Helpers.SetWindowCaptureVisibility(hwnd, visible: !excludeFromCapture);
    }
}
