using System.Reactive;
using Avalonia.Media;
using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Shared;

public interface IDrawingToolViewModel
{
    // Tool State
    AnnotationType CurrentAnnotationTool { get; set; }
    bool IsShapeToolActive { get; }
    bool IsPenToolActive { get; }
    bool IsTextToolActive { get; }

    // Style Properties
    Color SelectedColor { get; set; }
    double CurrentThickness { get; set; }
    double CurrentFontSize { get; set; }
    bool IsBold { get; set; }
    bool IsItalic { get; set; }
    System.Collections.ObjectModel.ObservableCollection<string> AvailableFonts { get; }
    string CurrentFontFamily { get; set; }
    System.Action FocusWindowAction { get; set; }
    
    // Text Entry State
    bool IsEnteringText { get; set; }
    string PendingText { get; set; }
    Avalonia.Point TextInputPosition { get; set; }

    // SnipWindow Specific (Optional via ShowIconSettings)
    bool ShowIconSettings { get; }
    double CornerIconScale { get; set; }
    double WingScale { get; set; }
    
    // Presets
    System.Collections.Generic.IEnumerable<Color> PresetColors { get; }

    // Commands
    ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; }
    ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; }
    ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; }
    ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; }
    
    ReactiveCommand<Unit, Unit> UndoCommand { get; }
    ReactiveCommand<Unit, Unit> RedoCommand { get; }
    
    ReactiveCommand<Color, Unit> ChangeColorCommand { get; }

    ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }
    
    ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }
    
    ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; }
    ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; }
    
    ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; }
    ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; }

    // Tooltip Hints
    string UndoTooltip { get; }
    string RedoTooltip { get; }
    string RectangleTooltip { get; }
    string EllipseTooltip { get; }
    string ArrowTooltip { get; }
    string LineTooltip { get; }
    string PenTooltip { get; }
    string TextTooltip { get; }
    string MosaicTooltip { get; }
    string BlurTooltip { get; }
}
