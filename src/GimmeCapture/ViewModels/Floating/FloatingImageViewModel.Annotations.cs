using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Linq;
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
        PushUndoAction(new BitmapHistoryAction(b => Image = b, Image, null));
    }

    protected override void Undo()
    {
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        
        if (action is BitmapHistoryAction bh && bh.NewBitmap == null)
        {
             var actionWithNew = new BitmapHistoryAction(bh.SetBitmapAction, bh.OldBitmap, Image);
             actionWithNew.Undo();
             _redoHistoryStack.Push(actionWithNew);
        }
        else
        {
            action.Undo();
            _redoHistoryStack.Push(action);
        }
        
        UpdateHistoryStatus();
    }

    protected override void Redo()
    {
        base.Redo();
    }
}
