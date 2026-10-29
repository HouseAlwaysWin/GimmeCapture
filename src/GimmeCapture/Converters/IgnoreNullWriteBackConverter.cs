using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace GimmeCapture.Converters;

/// <summary>
/// For a dropdown's SelectedItem bound both ways to a choice none of whose options is null: passes the value through,
/// but drops a null on its way back. A null there only means the dropdown lost its options, which happens when its
/// window closes: the Compress queue's dropdowns then wrote null into every built row's preset and output category,
/// and the category was saved, so the files came back without them after a restart.
/// </summary>
public sealed class IgnoreNullWriteBackConverter : IValueConverter
{
    public static readonly IgnoreNullWriteBackConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value ?? BindingOperations.DoNothing;
}
