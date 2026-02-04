using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using System.Windows.Input;

namespace GimmeCapture.Views.Controls;

public partial class CompactNumericStep : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<CompactNumericStep, double>(nameof(Value), defaultValue: 0, defaultBindingMode: BindingMode.TwoWay);

    static CompactNumericStep()
    {
        ValueProperty.Changed.AddClassHandler<CompactNumericStep>((s, e) => s.OnValueChanged(e));
    }

    private void OnValueChanged(AvaloniaPropertyChangedEventArgs e)
    {
        UpdateText();
    }

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
        
        var input = this.FindControl<TextBox>("ValueInput");
        if (input != null)
        {
            // Auto-select text on focus for easier editing
            input.GotFocus += (s, e) => 
            {
                input.SelectionStart = 0;
                input.SelectionEnd = input.Text?.Length ?? 0;
            };

            // Enter key clears focus to trigger logic
            input.KeyDown += (s, e) => 
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                {
                    this.Focus(); 
                }
            };

            // Validation on lost focus
            input.LostFocus += (s, e) => 
            {
                if (string.IsNullOrWhiteSpace(input.Text))
                {
                    Value = 0;
                    UpdateText();
                    return;
                }

                if (double.TryParse(input.Text, out double result))
                {
                    Value = result;
                }
                
                // Force text update to show formatted value (e.g. 1 -> 1.0)
                UpdateText();
            };
        }
    }

    private void UpdateText()
    {
        var input = this.FindControl<TextBox>("ValueInput");
        if (input != null)
        {
            input.Text = Value.ToString("0.##");
        }
    }

    private void OnIncreaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IncreaseCommand?.CanExecute(null) == true)
        {
            IncreaseCommand.Execute(null);
            UpdateText();
        }
    }

    private void OnDecreaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DecreaseCommand?.CanExecute(null) == true)
        {
            DecreaseCommand.Execute(null);
            UpdateText();
        }
    }
}
