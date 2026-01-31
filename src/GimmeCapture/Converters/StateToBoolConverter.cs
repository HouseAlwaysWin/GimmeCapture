using Avalonia.Data.Converters;
using GimmeCapture.ViewModels;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class StateToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SnipWindowViewModel.SnipState state)
        {
             // If we just want to know if "something is happening" vs Idle
             // Or if we check specifically for "Selected" or "Selecting"
             // Let's assume this is for visibility of the selection border, 
             // which should be visible during Selecting and Selected.
             
             return state == SnipWindowViewModel.SnipState.Selecting || 
                    state == SnipWindowViewModel.SnipState.Selected;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
