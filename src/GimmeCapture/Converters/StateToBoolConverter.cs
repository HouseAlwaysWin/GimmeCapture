using Avalonia.Data.Converters;
using GimmeCapture.ViewModels.Main;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class StateToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SnipState state)
        {
             // If we just want to know if "something is happening" vs Idle
             // Or if we check specifically for "Selected" or "Selecting"
             // Let's assume this is for visibility of the selection border, 
             // which should be visible during Selecting and Selected.
             
             return state == SnipState.Selecting || 
                    state == SnipState.Selected;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
