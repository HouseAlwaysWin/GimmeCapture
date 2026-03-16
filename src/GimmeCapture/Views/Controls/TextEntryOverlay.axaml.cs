using System;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;

namespace GimmeCapture.Views.Controls;

public partial class TextEntryOverlay : UserControl
{
    private IDisposable? _panelVisibleSubscription;

    public TextEntryOverlay()
    {
        InitializeComponent();
        
        var panel = this.FindControl<StackPanel>("TextEntryPanel");
        if (panel != null)
        {
            _panelVisibleSubscription = panel.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    var textBox = this.FindControl<TextBox>("TextInputOverlay");
                    textBox?.Focus();
                }
            });
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _panelVisibleSubscription?.Dispose();
        _panelVisibleSubscription = null;
        base.OnDetachedFromVisualTree(e);
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
