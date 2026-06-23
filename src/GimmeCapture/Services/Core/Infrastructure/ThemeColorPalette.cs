using Avalonia.Media;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Pure mapping from a theme accent color to its matching "deep" companion color.
/// Extracted from <c>MainWindowViewModel.Settings</c> so the mapping can be unit
/// tested without spinning up a ViewModel / UI thread. Behavior-preserving.
/// </summary>
public static class ThemeColorPalette
{
    private static readonly Color Gold = Color.Parse("#D4AF37");
    private static readonly Color GoldDeep = Color.Parse("#8B7500");
    private static readonly Color Silver = Color.Parse("#E0E0E0");
    private static readonly Color SilverDeep = Color.Parse("#606060");
    private static readonly Color DefaultDeep = Color.Parse("#900000");

    /// <summary>
    /// Returns the companion deep color for the given theme accent color.
    /// Unknown colors fall back to the default deep red.
    /// </summary>
    public static Color GetDeepColor(Color themeColor)
    {
        if (themeColor == Gold) return GoldDeep;
        if (themeColor == Silver) return SilverDeep;
        return DefaultDeep;
    }
}
