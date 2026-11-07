using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Avalonia;

/// <summary>
/// Avalonia's Inter (<c>fonts:Inter</c>: six weights, 1.9 MB), which the Fluent theme names first for text a window
/// gives no font of its own. Here that is only the processing, countdown and scroll-hint windows and the translation
/// layer: every other window draws in the app's own fonts. <c>WithInterFont</c> read all six faces as Avalonia was set
/// up, at every start: in a ReadyToRun probe 82-83 ms of set-up against 63-64.5 with this collection (76.5-78 against
/// 89-90 with the font manager's own set-up after it) and 2.9-3.6 MB of private bytes against none, and its assembly,
/// a 1.8 MB file, among those the launcher reads at sign-in. Here the same six load the first time the family is
/// asked for, so text in Inter looks as it did.
/// </summary>
internal sealed class DeferredInterFontCollection : FontCollectionBase
{
    internal const string KeyText = "fonts:Inter";

    // Where Avalonia.Fonts.Inter keeps the faces. Named only by this URI: a type of that assembly named in start-up
    // code would load it at start, which is what this collection is for not doing.
    internal const string SourceText = "avares://Avalonia.Fonts.Inter/Assets";

    private readonly object _gate = new();
    private volatile bool _loaded;

    public override Uri Key { get; } = new(KeyText);

    /// <summary>Whether the faces have been read: false until the family is first asked for.</summary>
    internal bool IsLoaded => _loaded;

    public override bool TryGetGlyphTypeface(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        EnsureLoaded();
        return base.TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);
    }

    public override bool TryGetFamilyTypefaces(
        string familyName,
        [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        EnsureLoaded();
        return base.TryGetFamilyTypefaces(familyName, out familyTypefaces);
    }

    public override bool TryMatchCharacter(
        int codepoint,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        string? familyName,
        CultureInfo? culture,
        out Typeface match)
    {
        EnsureLoaded();
        return base.TryMatchCharacter(codepoint, style, weight, stretch, familyName, culture, out match);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        // Text is laid out on more than one thread: a second one asking meanwhile waits for the faces.
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (!TryAddFontSource(new Uri(SourceText)))
                {
                    AppLog.Warning("Fonts.Inter", "No face of Inter could be read.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("Fonts.Inter", ex);
            }

            // Tried once whatever came of it, so a font that cannot be read is not tried again at every layout.
            _loaded = true;
        }
    }
}
