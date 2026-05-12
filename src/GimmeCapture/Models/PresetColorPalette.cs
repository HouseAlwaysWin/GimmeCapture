using System.Collections.Generic;
using Avalonia.Media;

namespace GimmeCapture.Models;

public static class PresetColorPalette
{
    public static IReadOnlyList<Color> DefaultColors { get; } =
    [
        Colors.Red,
        Colors.Green,
        Colors.Blue,
        Colors.Yellow,
        Colors.Cyan,
        Colors.Magenta,
        Colors.White,
        Colors.Black,
        Colors.Gray
    ];
}
