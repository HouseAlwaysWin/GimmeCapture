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
    private AnnotationSnapshot? _annotationEditBefore;
    private AnnotationHitZone _annotationHitZone;
    private Point _annotationDragStart;
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
    private IDisposable? _translationModeSubscription;
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

        // Keep the measured toolbar width current for ALL modes so the fixed top-center positioning
        // re-centers correctly (translation used to be the only mode that measured it).
        Toolbar.GetObservable(Visual.BoundsProperty).Subscribe(OnToolbarBoundsChanged);
    }

    private void OnToolbarBoundsChanged(Rect bounds)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (bounds.Width > 1)
        {
            _viewModel.ToolbarWidth = bounds.Width;
        }

        if (bounds.Height > 1)
        {
            _viewModel.ToolbarHeight = bounds.Height;
        }
    }
    
    private IReadOnlyList<Window> _hiddenTopmostWindows = [];

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Position logic ...
        
        // Defer Z-Order logic to ensure window is fully initialized
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            
            if (_viewModel != null)
            {
                _viewModel.VisualScaling = this.RenderScaling;
                _viewModel.ScreenOffset = this.Position;
                // Initialize Win32 Hook for click-through
                InitializeWin32Hook();

                // Populate AllScreenBounds for multi-monitor UI
                double scaling = this.RenderScaling;
                var allScreens = this.Screens.All;
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
                    
                    // NEW: Exclude from capture specifically for Translation Mode to prevent flickering during background OCR updates.
                    var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                    if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
                    {
                        Win32Helpers.SetWindowCaptureVisibility(hwnd, false);
                    }
                }
            }
            else
            {
                AppLog.Information("SnipWindow.OpenedWithoutViewModel");
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
                    AppLog.Warning("SnipWindow.RefreshWindowRects", ex);
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

                try
                {
                    _viewModel.AIScanCommand?.Execute().Subscribe();
                }
                catch (Exception ex)
                {
                    AppLog.Warning("SnipWindow.DeferredAIScan", ex);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
            
            // Avoid topmost nudging in translation mode because it can steal z-order
            // back from ComboBox popups and other transient UI.
            if (_viewModel?.IsTranslationMode != true)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(500);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        this.Topmost = false;
                        this.Topmost = true;
                        this.Activate();
                        this.Focus();
                    });
                }).Forget("SnipWindow.TopmostNudge");
            }

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
        PersistTranslatedSelectionsForClosingIfNeeded();
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
        _translationModeSubscription?.Dispose();
        _translationModeSubscription = null;
        
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

    private void PersistTranslatedSelectionsForClosingIfNeeded()
    {
        if (_viewModel?.IsTranslationMode != true)
        {
            return;
        }

        _viewModel.PersistTranslationSelectionsAction?.Invoke();
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

        _viewModel.RefreshInteractionRegion();
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
        if (_viewModel?.IsTranslationMode == true && IsTranslationSelectionModifierKeyEvent(e))
        {
            ApplyTranslationSelectionModifierState(true);
        }

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
        if (_viewModel != null
            && !e.Handled
            && !_viewModel.IsEnteringText
            && !textOrListInputFocused)
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
        if (_viewModel?.IsTranslationMode == true && IsTranslationSelectionModifierKeyEvent(e))
        {
            ApplyTranslationSelectionModifierState(false);
        }

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
