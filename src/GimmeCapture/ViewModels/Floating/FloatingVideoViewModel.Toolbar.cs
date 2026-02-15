using ReactiveUI;
using System.Reactive;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Collections.ObjectModel;
using System;
using System.Reactive.Linq;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : (CurrentTool == FloatingTool.Selection ? FloatingTool.None : CurrentTool);
    }

    public bool IsAnyToolActive => CurrentTool != FloatingTool.None || CurrentAnnotationTool != AnnotationType.None;

    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectionCommand { get; private set; } = null!;

    public override void UpdateToolbarPosition()
    {
        // Video toolbar uses Margin-based positioning in XAML, 
        // Logic can be added here if dynamic clamping is needed.
    }

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
             }
             CurrentAnnotationTool = targetTool;
        });
    }

     public void ToggleToolGroup(string group)
    {
        ToggleToolGroupCommand.Execute(group).Subscribe();
    }

    public void SelectTool(AnnotationType type)
    {
        CurrentAnnotationTool = type;
    }
}
