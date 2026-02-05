using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using GimmeCapture.Views.Main;
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

    public SnipWindow()
    {
        InitializeComponent();
        
        // Listen to pointer events on the window or canvas
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        
        // Text Input Events
        var textBox = this.FindControl<TextBox>("TextInputOverlay");
        if (textBox != null)
        {
            textBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    FinishTextEntry();
                    e.Handled = true;
                }
                // Allow normal Enter for new lines
                else if (e.Key == Key.Escape)
                {
                    CancelTextEntry();
                    e.Handled = true;
                }
            };
        }
        
        // Close on Escape
        KeyDown += OnKeyDown;
    }
    
    // Add Click Handler for OK Button
    private void OnTextConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FinishTextEntry();
    }

    private System.Collections.Generic.List<Window> _hiddenTopmostWindows = new();

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Position logic ...
        
        // Defer Z-Order logic to ensure window is fully initialized
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel != null)
            {
                _viewModel.VisualScaling = this.RenderScaling;
                _viewModel.ScreenOffset = this.Position;
                _viewModel.RefreshWindowRects(this.TryGetPlatformHandle()?.Handle);
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
            _viewModel.IsMagnifierEnabled = true;
            _viewModel.CloseAction = () => 
            {
                Close();
            };
            
            _viewModel.HideAction = () =>
            {
                Hide();
            };
            
            // Subscribe to SelectionRect, CurrentState, and IsDrawingMode changes to update window region
            // Added Throttle as per user request to prevent UI flickering during heavy updates
            _selectionRectSubscription = _viewModel.WhenAnyValue(
                x => x.SelectionRect, 
                x => x.CurrentState, 
                x => x.IsDrawingMode)
                .Throttle(TimeSpan.FromMilliseconds(16)) // ~60fps Limit
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(tuple => UpdateWindowRegion(tuple.Item1, tuple.Item2, tuple.Item3));
            


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

            _viewModel.OpenPinWindowAction = (bitmap, rect, color, thickness) =>
            {
                // Use settings directly from MainVm to ensure consistency
                bool hideDecoration = _viewModel.MainVm?.HideSnipPinDecoration ?? false;
                bool hideBorder = _viewModel.MainVm?.HideSnipPinBorder ?? false;
                
                var vm = new FloatingImageViewModel(bitmap, color, thickness, hideDecoration, hideBorder, _clipboardService);
                vm.WingScale = _viewModel.WingScale;
                vm.CornerIconScale = _viewModel.CornerIconScale;
                
                try
                {
                    // Calculate Window Size & Position based on the padding needed for decorations
                    // The 'rect' is the IMAGE position/size. The Window needs to be larger to hold the wings.
                    var padding = vm.WindowPadding;
                    
                    // Create Window FIRST so we can use it for TopLevel resolution
                    var win = new FloatingImageWindow
                    {
                        DataContext = vm,
                        Position = new PixelPoint((int)(rect.X - padding.Left), (int)(rect.Y - padding.Top)),
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
                                    bitmap.Save(stream);
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

            // NEW: Prevent resetting or closing if we are actively recording
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

    private void FinishTextEntry()
    {
        if (_viewModel == null || !_viewModel.IsEnteringText) return;
        
        if (!string.IsNullOrWhiteSpace(_viewModel.PendingText))
        {
            var relPoint = new Point(_viewModel.TextInputPosition.X - _viewModel.SelectionRect.X, _viewModel.TextInputPosition.Y - _viewModel.SelectionRect.Y);
            
            _viewModel.Annotations.Add(new Annotation
            {
                Type = AnnotationType.Text,
                StartPoint = relPoint,
                EndPoint = relPoint,
                Text = _viewModel.PendingText,
                Color = _viewModel.SelectedColor,
                FontSize = _viewModel.CurrentFontSize,
                FontFamily = _viewModel.CurrentFontFamily,
                IsBold = _viewModel.IsBold,
                IsItalic = _viewModel.IsItalic
            });
        }
        
        CancelTextEntry();
    }

    private void CancelTextEntry()
    {
        var vm = _viewModel;
        if (vm == null) return;
        vm.IsEnteringText = false;
        vm.PendingText = string.Empty;
        _lastTextFinishTime = DateTime.Now; // Set debounce timestamp
        // Shift focus back to window to allow hotkeys etc.
        this.Focus();
    }
}
