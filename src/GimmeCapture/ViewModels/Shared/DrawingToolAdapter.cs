using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Shared;

/// <summary>
/// An <see cref="IDrawingToolViewModel"/> over a composable <see cref="AnnotationEditorState"/>, so the
/// shared drawing controls (<c>DrawingToolbar</c>, <c>TextEntryOverlay</c>) work in hosts that don't derive
/// from <c>FloatingWindowViewModelBase</c> — the compress 進階影片編輯 editor. Mirrors the base VM's
/// delegation, change-forwarding, and command wiring (same Tip* localization keys and hotkey hints).
/// </summary>
public sealed class DrawingToolAdapter : ReactiveObject, IDrawingToolViewModel, IDisposable
{
    private readonly AnnotationEditorState _state;
    private readonly IDisposable _stateSubscription;

    public DrawingToolAdapter(AnnotationEditorState state)
    {
        _state = state;

        _stateSubscription = _state.Changed.Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(CurrentAnnotationTool));
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsRedactionToolActive));
            this.RaisePropertyChanged(nameof(SelectedAnnotation));
            this.RaisePropertyChanged(nameof(StyleAnnotationTool));
            this.RaisePropertyChanged(nameof(SelectedColor));
            this.RaisePropertyChanged(nameof(CurrentThickness));
            this.RaisePropertyChanged(nameof(CurrentFontSize));
            this.RaisePropertyChanged(nameof(CurrentFontFamily));
            this.RaisePropertyChanged(nameof(IsBold));
            this.RaisePropertyChanged(nameof(IsItalic));
            this.RaisePropertyChanged(nameof(IsShapeFilled));
            this.RaisePropertyChanged(nameof(IsEnteringText));
            this.RaisePropertyChanged(nameof(PendingText));
            this.RaisePropertyChanged(nameof(TextInputPosition));
            this.RaisePropertyChanged(nameof(HasUndo));
            this.RaisePropertyChanged(nameof(HasRedo));
            this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
        });

        var notEnteringText = this.WhenAnyValue(x => x.IsEnteringText)
            .Select(x => !x).ObserveOn(RxSchedulers.MainThreadScheduler);

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(_state.SelectTool, notEnteringText);
        ToggleToolGroupCommand = ReactiveCommand.Create<string>(_state.ToggleToolGroup, notEnteringText);
        ClearAnnotationsCommand = ReactiveCommand.Create(_state.ClearAnnotations, notEnteringText);

        ConfirmTextEntryCommand = ReactiveCommand.Create(() =>
        {
            _state.ConfirmTextEntry();
            FocusWindowAction?.Invoke();
        });
        CancelTextEntryCommand = ReactiveCommand.Create(() =>
        {
            _state.CancelTextEntry();
            FocusWindowAction?.Invoke();
        });

        var canUndo = this.WhenAnyValue(x => x.HasUndo, x => x.IsEnteringText, (u, t) => u && !t)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        UndoCommand = ReactiveCommand.Create(_state.Undo, canUndo);
        var canRedo = this.WhenAnyValue(x => x.HasRedo, x => x.IsEnteringText, (r, t) => r && !t)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        RedoCommand = ReactiveCommand.Create(_state.Redo, canRedo);

        var canActOnSelected = this.WhenAnyValue(
                x => x.SelectedAnnotation, x => x.IsEnteringText,
                (Annotation? annotation, bool enteringText) => annotation != null && !enteringText)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        BringToFrontCommand = ReactiveCommand.Create(() => { _state.BringSelectedToFront(); }, canActOnSelected);
        SendToBackCommand = ReactiveCommand.Create(() => { _state.SendSelectedToBack(); }, canActOnSelected);
        DeleteSelectedAnnotationCommand = ReactiveCommand.Create(
            () => { _state.RemoveSelectedAnnotation(); }, canActOnSelected);

        ChangeColorCommand = ReactiveCommand.Create<Color>(_state.ApplySelectedColor);
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Min(CurrentFontSize + 2, 72); });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Max(CurrentFontSize - 2, 8); });
        IncreaseThicknessCommand = ReactiveCommand.Create(() => _state.ApplySelectedThickness(Math.Min(CurrentThickness + 1, 30)));
        DecreaseThicknessCommand = ReactiveCommand.Create(() => _state.ApplySelectedThickness(Math.Max(CurrentThickness - 1, 1)));
        SetRedactionPresetCommand = ReactiveCommand.Create<string>(_state.SetRedactionPreset);
    }

    // ── Tool state (delegating) ──
    public AnnotationType CurrentAnnotationTool
    {
        get => _state.CurrentAnnotationTool;
        set => _state.CurrentAnnotationTool = value;
    }

    public bool IsShapeToolActive => _state.IsShapeToolActive;
    public bool IsPenToolActive => _state.IsPenToolActive;
    public bool IsTextToolActive => _state.IsTextToolActive;
    public bool IsRedactionToolActive => _state.IsRedactionToolActive;
    public Annotation? SelectedAnnotation => _state.SelectedAnnotation;
    public AnnotationType StyleAnnotationTool => _state.StyleAnnotationTool;

    // ── Style (delegating) ──
    public Color SelectedColor { get => _state.SelectedColor; set => _state.SelectedColor = value; }
    public double CurrentThickness { get => _state.CurrentThickness; set => _state.CurrentThickness = value; }
    public double CurrentFontSize { get => _state.CurrentFontSize; set => _state.CurrentFontSize = value; }
    public bool IsBold { get => _state.IsBold; set => _state.IsBold = value; }
    public bool IsItalic { get => _state.IsItalic; set => _state.IsItalic = value; }
    public bool IsShapeFilled { get => _state.CurrentFill; set => _state.CurrentFill = value; }
    public FontFamily CurrentFontFamily { get => _state.CurrentFontFamily; set => _state.CurrentFontFamily = value; }

    public ObservableCollection<FontFamily> AvailableFonts { get; } = new()
    {
        new FontFamily("Arial"),
        new FontFamily("Segoe UI"),
        new FontFamily("Consolas"),
        new FontFamily("Times New Roman"),
        new FontFamily("Comic Sans MS"),
        new FontFamily("Microsoft JhengHei"),
        new FontFamily("Meiryo")
    };

    public Action? FocusWindowAction { get; set; }

    // ── Text entry (delegating) ──
    public bool IsEnteringText { get => _state.IsEnteringText; set => _state.IsEnteringText = value; }
    public string PendingText { get => _state.PendingText; set => _state.PendingText = value; }
    public Avalonia.Point TextInputPosition { get => _state.TextInputPosition; set => _state.TextInputPosition = value; }

    // ── Host-specific extras (not used by the compress editor) ──
    public bool ShowIconSettings => false;
    public double WingScale { get; set; } = 1.0;

    public IEnumerable<Color> PresetColors => PresetColorPalette.DefaultColors;

    public bool HasUndo => _state.HasUndo;
    public bool HasRedo => _state.HasRedo;
    public string CurrentRedactionPreset => _state.CurrentRedactionPreset;

    // ── Commands ──
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> BringToFrontCommand { get; }
    public ReactiveCommand<Unit, Unit> SendToBackCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedAnnotationCommand { get; }
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }
    public ReactiveCommand<string, Unit> SetRedactionPresetCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => { });

    // ── Tooltips (same keys/hotkey hints as the Pin windows) ──
    public string UndoTooltip => $"{LocalizationService.Instance["Undo"]} (Ctrl+Z)";
    public string RedoTooltip => $"{LocalizationService.Instance["Redo"]} (Ctrl+Y)";
    public string RectangleTooltip => $"{LocalizationService.Instance["TipRectangle"]} (R)";
    public string EllipseTooltip => $"{LocalizationService.Instance["TipEllipse"]} (E)";
    public string ArrowTooltip => $"{LocalizationService.Instance["TipArrow"]} (A)";
    public string LineTooltip => $"{LocalizationService.Instance["TipLine"]} (L)";
    public string PenTooltip => $"{LocalizationService.Instance["TipPen"]} (P)";
    public string TextTooltip => $"{LocalizationService.Instance["TipText"]} (T)";
    public string CalloutTooltip => LocalizationService.Instance["TipCallout"];
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} (M)";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} (B)";
    public string HighlighterTooltip => LocalizationService.Instance["TipHighlighter"];
    public string StepTooltip => LocalizationService.Instance["TipStep"];
    public string BringToFrontTooltip => LocalizationService.Instance["TipBringToFront"];
    public string SendToBackTooltip => LocalizationService.Instance["TipSendToBack"];

    public void Dispose() => _stateSubscription.Dispose();
}
