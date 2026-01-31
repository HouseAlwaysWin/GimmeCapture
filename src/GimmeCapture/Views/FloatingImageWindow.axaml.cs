using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using GimmeCapture.ViewModels;
using System;

namespace GimmeCapture.Views;

public partial class FloatingImageWindow : Window
{
    public FloatingImageWindow()
    {
        InitializeComponent();
        
        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingImageViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
    
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
