using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using System.Windows.Input;

namespace GimmeCapture.Views.Controls;

public partial class CompactNumericStep : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CompactNumericStep, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly StyledProperty<ICommand> IncreaseCommandProperty =
        AvaloniaProperty.Register<CompactNumericStep, ICommand>(nameof(IncreaseCommand));

    public ICommand IncreaseCommand
    {
        get => GetValue(IncreaseCommandProperty);
        set => SetValue(IncreaseCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand> DecreaseCommandProperty =
        AvaloniaProperty.Register<CompactNumericStep, ICommand>(nameof(DecreaseCommand));

    public ICommand DecreaseCommand
    {
        get => GetValue(DecreaseCommandProperty);
        set => SetValue(DecreaseCommandProperty, value);
    }

    public CompactNumericStep()
    {
        InitializeComponent();
    }

    private void OnIncreaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IncreaseCommand?.CanExecute(null) == true)
        {
            IncreaseCommand.Execute(null);
        }
    }

    private void OnDecreaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DecreaseCommand?.CanExecute(null) == true)
        {
            DecreaseCommand.Execute(null);
        }
    }
}
