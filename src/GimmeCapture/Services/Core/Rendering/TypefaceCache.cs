using System.Collections.Concurrent;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Rendering;

/// <summary>
/// Process-wide cache of immutable <see cref="SKTypeface"/> instances keyed by (family, weight, slant).
/// <see cref="SKTypeface.FromFamilyName(string, SKFontStyle)"/> performs a font lookup on every call, and
/// annotation / translation rendering builds one per text draw — caching avoids the repeated lookup and
/// managed allocation. Entries live for the app lifetime (a small, bounded set of fonts) and are shared,
/// so callers must treat the result as read-only and must NOT dispose it (dispose the wrapping SKFont only).
/// </summary>
public static class TypefaceCache
{
    private static readonly ConcurrentDictionary<(string family, SKFontStyleWeight weight, SKFontStyleSlant slant), SKTypeface> _cache = new();

    public static SKTypeface Get(
        string family,
        SKFontStyleWeight weight = SKFontStyleWeight.Normal,
        SKFontStyleSlant slant = SKFontStyleSlant.Upright)
    {
        return _cache.GetOrAdd((family ?? string.Empty, weight, slant), static key =>
            SKTypeface.FromFamilyName(key.family, new SKFontStyle(key.weight, SKFontStyleWidth.Normal, key.slant))
            ?? SKTypeface.Default);
    }
}
