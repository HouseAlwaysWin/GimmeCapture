using System;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;

namespace GimmeCapture.Views.Controls;

public partial class TextEntryOverlay : UserControl
{
    public TextEntryOverlay()
    {
        InitializeComponent();
        
        var panel = this.FindControl<StackPanel>("TextEntryPanel");
        if (panel != null)
        {
            panel.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    var textBox = this.FindControl<TextBox>("TextInputOverlay");
                    textBox?.Focus();
                }
            });
        }
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not IDrawingToolViewModel vm) return;

        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            vm.ConfirmTextEntryCommand.Execute(Unit.Default).Subscribe();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelTextEntryCommand.Execute(Unit.Default).Subscribe();
            e.Handled = true;
        }
    }
}
