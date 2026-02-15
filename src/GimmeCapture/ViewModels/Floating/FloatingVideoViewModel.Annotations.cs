using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // All annotation logic is now in FloatingWindowViewModelBase.
    
    // Proxies for View Binding compatibility
    // If the View binds to CanUndo/CanRedo, we map them to Base properties.
    public bool CanUndo => HasUndo;
    public bool CanRedo => HasRedo;
}
