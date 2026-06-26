using System;
using Avalonia;

namespace GimmeCapture.Models;

/// <summary>
/// A top-level window that can be picked as a recording target. <see cref="Bounds"/> is in physical
/// screen pixels (the window's current rectangle); the caller converts it to the overlay's logical
/// coordinate space before using it as a selection rect.
/// </summary>
public sealed record RecordableWindow(string Title, Rect Bounds, IntPtr Hwnd);
