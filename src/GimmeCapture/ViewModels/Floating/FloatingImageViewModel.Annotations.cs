using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Reactive.Linq;
using System;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    // Base has: IsShapeToolActive, IsPenToolActive, IsTextToolActive
    // Base has: IsEnteringText, PendingText, TextInputPosition, CurrentFontFamily
    // Base has: Thicknesses, AvailableFonts, PresetColors
    // Base has: Commands (IncreaseFontSize, etc.)



    // AddAnnotation, ClearAnnotations are in Base.
    // PushResizeAction is in Base.
    // PushUndoAction is in Base.
    
    // Action to set window rect is in Base (RequestSetWindowRect)
    // PushUndoState is Specific to Image (it uses Image property)
    private void PushUndoState()
    {
        if (Image == null) return;
        // Correctly capture current state for undo
        PushUndoAction(new BitmapHistoryAction(b => Image = b, Image, null, getCurrentBitmap: () => Image));
    }

    protected override void Undo()
    {
        if (EditorState.HistoryStack.Count == 0) return;
        var action = EditorState.HistoryStack.Pop();
        
        if (action is BitmapHistoryAction bh && bh.NewBitmap == null)
        {
             var actionWithNew = new BitmapHistoryAction(bh.SetBitmapAction, bh.OldBitmap, Image, getCurrentBitmap: () => Image);
             actionWithNew.Undo();
             EditorState.RedoHistoryStack.Push(actionWithNew);
        }
        else
        {
            action.Undo();
            EditorState.RedoHistoryStack.Push(action);
        }
        
        UpdateHistoryStatus();
    }

    protected override void Redo()
    {
        base.Redo();
    }
}
