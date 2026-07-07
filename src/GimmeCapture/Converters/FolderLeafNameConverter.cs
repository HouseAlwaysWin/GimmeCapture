using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace GimmeCapture.Converters;

/// <summary>
/// Displays a folder path as just its leaf name (the category name), e.g. "D:\影片2\分類A" → "分類A".
/// Used by the Compress row's output-category dropdown so the list shows short category names, not full paths.
/// A null/empty value passes through unchanged (the template shows a localized "default" label for empty).
/// </summary>
public sealed class FolderLeafNameConverter : IValueConverter
{
    public static readonly FolderLeafNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0)
        {
            return value;
        }

        string leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return leaf.Length > 0 ? leaf : path;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
