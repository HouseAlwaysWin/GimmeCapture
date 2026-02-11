using ReactiveUI;
using System.Reactive;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Collections.ObjectModel;
using System;
using System.Reactive.Linq;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    private FloatingTool _currentTool = FloatingTool.None;
    public FloatingTool CurrentTool
    {
        get => _currentTool;
        set 
        {
            if (_currentTool == value) return;
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Tool changing: {_currentTool} -> {value}");
            
            // Cleanup previous tool state
            if (_currentTool == FloatingTool.PointRemoval)
            {
                IsInteractiveSelectionMode = false;
                InteractiveMask = null;
                _interactivePoints.Clear();
            }
            else if (_currentTool == FloatingTool.Selection)
            {
                SelectionRect = new Avalonia.Rect();
            }

            if (value != FloatingTool.None)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }

            this.RaiseAndSetIfChanged(ref _currentTool, value);
            
            // Notify UI properties
            this.RaisePropertyChanged(nameof(IsSelectionMode));
            this.RaisePropertyChanged(nameof(IsPointRemovalMode));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
            
            // Initialization for new tool
            if (value == FloatingTool.PointRemoval)
            {
                _ = StartInteractiveRemovalAsync();
            }
        }
    }

    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : (CurrentTool == FloatingTool.Selection ? FloatingTool.None : CurrentTool);
    }

    public bool IsPointRemovalMode
    {
        get => CurrentTool == FloatingTool.PointRemoval;
        set {
            if (CurrentTool == FloatingTool.PointRemoval && !value)
            {
                // We keep SAM2Service alive for the window lifetime for fast re-entry
                IsInteractiveSelectionMode = false;
            }
            CurrentTool = value ? FloatingTool.PointRemoval : (CurrentTool == FloatingTool.PointRemoval ? FloatingTool.None : CurrentTool);
        }
    }

    public bool IsAnyToolActive => CurrentTool != FloatingTool.None || CurrentAnnotationTool != AnnotationType.None;

    private bool _showToolbar = false;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set
        {
            this.RaiseAndSetIfChanged(ref _showToolbar, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private Avalonia.Thickness _toolbarMargin = new Avalonia.Thickness(0, 0, 0, 10);
    public Avalonia.Thickness ToolbarMargin
    {
        get => _toolbarMargin;
        set => this.RaiseAndSetIfChanged(ref _toolbarMargin, value);
    }
    
    // Position properties
    private double _toolbarTop;
    public double ToolbarTop
    {
        get => _toolbarTop;
        set => this.RaiseAndSetIfChanged(ref _toolbarTop, value);
    }

    private double _toolbarLeft;
    public double ToolbarLeft
    {
        get => _toolbarLeft;
        set => this.RaiseAndSetIfChanged(ref _toolbarLeft, value);
    }

    private double _toolbarWidth;
    public double ToolbarWidth
    {
        get => _toolbarWidth;
        set 
        {
            this.RaiseAndSetIfChanged(ref _toolbarWidth, value);
            UpdateToolbarPosition();
        }
    }

    private double _toolbarHeight;
    public double ToolbarHeight
    {
        get => _toolbarHeight;
        set 
        {
            this.RaiseAndSetIfChanged(ref _toolbarHeight, value);
            UpdateToolbarPosition();
        }
    }
    
    private bool _isToolbarFlipped;
    public bool IsToolbarFlipped
    {
        get => _isToolbarFlipped;
        set => this.RaiseAndSetIfChanged(ref _isToolbarFlipped, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; private set; } = null!;
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; private set; } = null!;
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectionCommand { get; private set; } = null!;

    private void InitializeToolbarCommands()
    {
        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });

        SelectionCommand = ReactiveCommand.Create(() => 
        {
            CurrentTool = CurrentTool == FloatingTool.Selection ? FloatingTool.None : FloatingTool.Selection;
        });

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(tool => 
        {
            var targetTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
            if (targetTool != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
                IsPointRemovalMode = false;
                IsInteractiveSelectionMode = false;
            }
            CurrentAnnotationTool = targetTool;
        });

        ToggleToolGroupCommand = ReactiveCommand.Create<string>(group => 
        {
             AnnotationType targetTool = AnnotationType.None;
             if (group == "Shapes")
             {
                 targetTool = IsShapeToolActive ? AnnotationType.None : AnnotationType.Rectangle;
             }
             else if (group == "Pen")
             {
                 targetTool = (CurrentAnnotationTool == AnnotationType.Pen) ? AnnotationType.None : AnnotationType.Pen;
             }
             else if (group == "Text")
             {
                 targetTool = IsTextToolActive ? AnnotationType.None : AnnotationType.Text;
             }

             if (targetTool != AnnotationType.None)
             {
                 CurrentTool = FloatingTool.None;
                 IsPointRemovalMode = false;
                 IsInteractiveSelectionMode = false;
             }
             CurrentAnnotationTool = targetTool;
        });
    }

    private void UpdateToolbarPosition()
    {
        if (!ShowToolbar || ToolbarWidth <= 0 || ToolbarHeight <= 0) return;

        // Toolbar is relative to the window content
        // Default: Bottom, Centered
        double paddingLeft = WindowPadding.Left;
        double paddingTop = WindowPadding.Top;
        double paddingBottom = WindowPadding.Bottom;
        
        // Horizontal centering logic
        // We want the toolbar to be centered relative to the WINDOW, including padding/decorations.
        // ToolbarLeft = (TotalComputedWindowWidth - ToolbarWidth) / 2
        // But since we are inside a Canvas or Panel, relative coords matter.
        // Assuming parent container is the window content root.
        
        double left = (DisplayWidth + paddingLeft + WindowPadding.Right - ToolbarWidth) / 2;
        
        // Edge Clamping Logic (Horizontal)
        // If window is very narrow, toolbar might go negative.
        // Clamp to 0.
        if (left < 0) left = 0;
        
        // Vertical Positioning
        // Default position: Below the content (in the bottom padding area)
        double top = DisplayHeight + paddingTop + 5; 

        bool shouldFlip = false;

        // Verify with screen bounds to see if we should flip
        if (ScreenPosition.HasValue)
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var window = desktop?.Windows.FirstOrDefault(w => w.DataContext == this);
            if (window != null && window.Screens != null)
            {
                var screen = window.Screens.ScreenFromVisual(window) ?? window.Screens.Primary;
                if (screen != null)
                {
                    // Calculate toolbar's absolute bottom
                    // window.Position is in Physical pixels.
                    double scaling = screen.Scaling;
                    // top is relative to window top-left (inside client area). 
                    // Window Position is Top-Left of window.
                    double absTop = window.Position.Y + (top * scaling);
                    double absBottom = absTop + (ToolbarHeight * scaling);

                    // If it goes beyond screen work area bottom, flip to top
                    if (absBottom > screen.WorkingArea.Bottom - (10 * scaling))
                    {
                        shouldFlip = true;
                    }
                }
            }
        }
        
        if (shouldFlip)
        {
             // Position *above* content
             // Window Top Padding usually handles title bar etc.
             // We want it above the top padding if possible, or inside top padding?
             // Relative to content (0,0 is top-left of content with padding).
             // top = -ToolbarHeight - 5; 
             IsToolbarFlipped = true;
             
             // If we flip, it sits "above" the display area.
             // If our top padding isn't huge, this draws OVER title bar or off-window?
             // It draws in the extended window area (transparent window).
             top = paddingTop - ToolbarHeight - 5;
        }
        else
        {
            IsToolbarFlipped = false;
        }

        ToolbarLeft = left;
        ToolbarTop = top;
    }

    public void ToggleToolGroup(string group)
    {
        // Wrapper for internal command logic if needed externally
        ToggleToolGroupCommand.Execute(group).Subscribe();
    }

    public void SelectTool(AnnotationType type)
    {
        CurrentAnnotationTool = type;
    }
}
