using Avalonia;
using Avalonia.Media;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using GimmeCapture.Models;

namespace GimmeCapture.ViewModels.Floating;

public abstract class FloatingWindowViewModelBase : ViewModelBase, IDisposable
{
    // Border
    private Color _borderColor = Colors.Red;
    public Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    private double _borderThickness = 2.0;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private bool _hidePinDecoration = false;
    public bool HidePinDecoration
    {
        get => _hidePinDecoration;
        set
        {
            this.RaiseAndSetIfChanged(ref _hidePinDecoration, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
    }

    // Dimensions
    private double _originalWidth;
    public double OriginalWidth
    {
        get => _originalWidth;
        set => this.RaiseAndSetIfChanged(ref _originalWidth, value);
    }

    private double _originalHeight;
    public double OriginalHeight
    {
        get => _originalHeight;
        set => this.RaiseAndSetIfChanged(ref _originalHeight, value);
    }

    private double _displayWidth;
    public double DisplayWidth
    {
        get => _displayWidth;
        set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
    }

    private double _displayHeight;
    public double DisplayHeight
    {
        get => _displayHeight;
        set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
    }

    // Decoration Scale
    private double _wingScale = 1.0;
    public double WingScale
    {
        get => _wingScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _wingScale, value);
            this.RaisePropertyChanged(nameof(WingWidth));
            this.RaisePropertyChanged(nameof(WingHeight));
            this.RaisePropertyChanged(nameof(LeftWingMargin));
            this.RaisePropertyChanged(nameof(RightWingMargin));
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private double _cornerIconScale = 1.0;
    public double CornerIconScale
    {
        get => _cornerIconScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _cornerIconScale, value);
            this.RaisePropertyChanged(nameof(SelectionIconSize));
        }
    }

    // Derived Decoration Props
    public double WingWidth => 100 * WingScale;
    public double WingHeight => 60 * WingScale;
    public double SelectionIconSize => 22 * CornerIconScale;
    public Thickness LeftWingMargin => new Thickness(-WingWidth, 0, 0, 0);
    public Thickness RightWingMargin => new Thickness(0, 0, -WingWidth, 0);

    // Padding
    public abstract Thickness WindowPadding { get; }

    // Toolbar Properties
    private bool _showToolbar = true;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set 
        {
            this.RaiseAndSetIfChanged(ref _showToolbar, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
            UpdateToolbarPosition();
        }
    }

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

    private Thickness _toolbarMargin = new Thickness(0, 0, 0, 10);
    public Thickness ToolbarMargin
    {
        get => _toolbarMargin;
        set => this.RaiseAndSetIfChanged(ref _toolbarMargin, value);
    }

    private bool _isToolbarFlipped;
    public bool IsToolbarFlipped
    {
        get => _isToolbarFlipped;
        set => this.RaiseAndSetIfChanged(ref _isToolbarFlipped, value);
    }

    // Tools & Annotations
    private FloatingTool _currentTool = FloatingTool.None;
    public virtual FloatingTool CurrentTool
    {
        get => _currentTool;
        set => this.RaiseAndSetIfChanged(ref _currentTool, value);
    }
    
    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public virtual AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set => this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
    }

    public ObservableCollection<Annotation> Annotations { get; } = new();

    private Avalonia.Media.Color _selectedColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
    }

    private double _currentThickness = 4.0;
    public double CurrentThickness
    {
        get => _currentThickness;
        set => this.RaiseAndSetIfChanged(ref _currentThickness, value);
    }

    private double _currentFontSize = 24.0;
    public double CurrentFontSize
    {
        get => _currentFontSize;
        set => this.RaiseAndSetIfChanged(ref _currentFontSize, value);
    }
    
    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set => this.RaiseAndSetIfChanged(ref _isBold, value);
    }

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set => this.RaiseAndSetIfChanged(ref _isItalic, value);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; protected set; } = null!;
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> UndoCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> RedoCommand { get; protected set; } = null!;

    // History
    protected Stack<IHistoryAction> _historyStack = new();
    protected Stack<IHistoryAction> _redoHistoryStack = new();
    
    private bool _hasUndo;
    public bool HasUndo
    {
        get => _hasUndo;
        protected set => this.RaiseAndSetIfChanged(ref _hasUndo, value);
    }

    private bool _hasRedo;
    public bool HasRedo
    {
        get => _hasRedo;
        protected set => this.RaiseAndSetIfChanged(ref _hasRedo, value);
    }

    // Position
    private PixelPoint? _screenPosition;
    public PixelPoint? ScreenPosition
    {
        get => _screenPosition;
        set 
        {
            this.RaiseAndSetIfChanged(ref _screenPosition, value);
            UpdateToolbarPosition();
        }
    }

    // Hotkeys (Virtual to allow overriding or service-based values)
    public virtual string CopyHotkey => "Ctrl+C";
    public virtual string PinHotkey => "F3";
    public virtual string UndoHotkey => "Ctrl+Z";
    public virtual string RedoHotkey => "Ctrl+Y";
    public virtual string ClearHotkey => "Delete";
    public virtual string SaveHotkey => "Ctrl+S";
    public virtual string CloseHotkey => "Escape";
    
    public virtual string RectangleHotkey => "R";
    public virtual string EllipseHotkey => "E";
    public virtual string ArrowHotkey => "A";
    public virtual string LineHotkey => "L";
    public virtual string PenHotkey => "P";
    public virtual string TextHotkey => "T";
    public virtual string MosaicHotkey => "M";
    public virtual string BlurHotkey => "B";

    // Scale Commands
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});

    public virtual void UpdateToolbarPosition() { }

    protected void UpdateHistoryStatus()
    {
        HasUndo = _historyStack.Count > 0;
        HasRedo = _redoHistoryStack.Count > 0;
    }

    public virtual void Dispose() {} 
}
