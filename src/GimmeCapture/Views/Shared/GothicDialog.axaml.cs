using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;

namespace GimmeCapture.Views.Shared;

public partial class GothicDialog : ReactiveWindow<GothicDialogViewModel>
{
    public GothicDialog()
    {
        InitializeComponent();
        
        this.WhenActivated(d =>
        {
            if (ViewModel != null)
            {
                d(ViewModel.CloseCommand.Subscribe(result => this.Close(result)));
            }
        });

        // Allow dragging from the background/border
        PointerPressed += (s, e) => BeginMoveDrag(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
