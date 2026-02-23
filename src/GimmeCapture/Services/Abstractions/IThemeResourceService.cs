using Avalonia.Media;

namespace GimmeCapture.Services.Abstractions;

public interface IThemeResourceService
{
    void UpdateThemeColors(Color accentColor, Color deepColor);
}
