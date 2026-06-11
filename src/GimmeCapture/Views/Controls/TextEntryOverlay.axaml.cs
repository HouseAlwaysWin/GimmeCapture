using System;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
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
        
        _panelVisibleSubscription = this.GetObservable(IsVisibleProperty).Subscribe(visible =>
        {
            if (visible)
            {
                FocusTextInput();
            }
        });
    }

    public void FocusTextInput()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var textBox = this.FindControl<TextBox>("TextInputOverlay");
                if (textBox == null) return;

                if (TopLevel.GetTopLevel(this) is Window window)
                {
                    window.Activate();
                }

                textBox.Focus();
                textBox.CaretIndex = textBox.Text?.Length ?? 0;
            },
            DispatcherPriority.Input);
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
