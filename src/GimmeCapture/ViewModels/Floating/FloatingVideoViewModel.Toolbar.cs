using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // IsSelectionMode, IsAnyToolActive are in Base.
    // Commands are in Base.

    public override void UpdateToolbarPosition()
    {
        // Video toolbar uses Margin-based positioning in XAML, 
        // Logic can be added here if dynamic clamping is needed.
    }

    private void InitializeToolbarCommands()
    {
        // Handled by Base InitializeBaseCommands
    }

     public void ToggleToolGroup(string group)
     {
         // This might be called from View? 
         // If so, we should use the Command. 
         // If View calls this method directly, we need to implement it or redirect to Command.
         // Base has ToggleToolGroupCommand. 
         
         ToggleToolGroupCommand.Execute(group).Subscribe();
     }
}
