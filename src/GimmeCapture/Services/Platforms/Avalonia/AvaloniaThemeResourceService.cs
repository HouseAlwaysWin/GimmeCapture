using Avalonia;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Desktop;

public class AvaloniaThemeResourceService : IThemeResourceService
{
    public void UpdateThemeColors(Color accentColor, Color deepColor)
    {
        if (Application.Current?.Resources is { } resources)
        {
            resources["ThemeAccentColor"] = accentColor;
            resources["ThemeDeepColor"] = deepColor;
        }
    }
}
