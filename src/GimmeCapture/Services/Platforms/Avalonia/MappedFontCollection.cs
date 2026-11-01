using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using GimmeCapture.Services.Core.Infrastructure;
using SkiaSharp;

namespace GimmeCapture.Services.Platforms.Avalonia;

/// <summary>
/// The app's CJK fonts, Noto Serif TC and JP (16.8 and 12.9 MB), as the font collection <c>fonts:GimmeCapture</c>, read in
/// place from the app's assembly. A font loaded from avares:// goes through SkiaSharp's <c>SKTypeface.FromStream</c>,
/// which copies the whole file into native memory: 17 MB of commit for the Chinese UI before a character was drawn,
/// and as much again for the bold the UI draws, which Avalonia synthesizes from a copy of the font's stream (each font
/// has one face, ExtraLight). Here each font is an <see cref="SKData"/> over its manifest resource, bytes in the
/// assembly's file-backed image, and a synthetic face is another typeface over the same bytes, so nothing is copied.
/// Avalonia.Skia's typeface wrapper is internal and is built by reflection; without it, or where the resource is not in
/// memory already, a font is read through a stream, a copy, as avares:// did.
/// </summary>
internal sealed class MappedFontCollection : FontCollectionBase
{
    internal const string KeyText = "fonts:GimmeCapture";
    internal const string ChineseResource = "GimmeCapture.Fonts.NotoSerifTC-VariableFont_wght.ttf";
    internal const string JapaneseResource = "GimmeCapture.Fonts.NotoSerifJP-VariableFont_wght.ttf";

    // Avalonia.Skia.SkiaTypeface(SKTypeface, FontSimulations): internal type, public constructor (Avalonia 12).
    internal static readonly ConstructorInfo? SkiaTypefaceConstructor =
        Type.GetType("Avalonia.Skia.SkiaTypeface, Avalonia.Skia")?.GetConstructor([typeof(SKTypeface), typeof(FontSimulations)]);

    private static bool _registered;

    // The bytes behind each face read in place, synthetic ones included, to make further faces from.
    private readonly Dictionary<GlyphTypeface, SKData> _inPlace = new(ReferenceEqualityComparer.Instance);

    private MappedFontCollection(Assembly assembly, IEnumerable<string> resources)
    {
        foreach (string resource in resources)
        {
            Add(assembly, resource);
        }
    }

    public override Uri Key { get; } = new(KeyText);

    /// <summary>
    /// Adds the collection to Avalonia's font manager, once: call after the platform is set up and before any text is laid
    /// out in a CJK font.
    /// </summary>
    internal static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        try
        {
            FontManager.Current.AddFontCollection(
                new MappedFontCollection(typeof(MappedFontCollection).Assembly, [ChineseResource, JapaneseResource]));
        }
        catch (Exception ex)
        {
            AppLog.Warning("Fonts.Register", ex);
        }
    }

    /// <summary>
    /// A bold or oblique face of a font read in place, as another typeface over the same bytes. Avalonia makes one from
    /// the font's stream, which reads the whole file into an array and has SkiaSharp copy it once more.
    /// </summary>
    public override bool TryCreateSyntheticGlyphTypeface(
        GlyphTypeface glyphTypeface,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        if (!_inPlace.TryGetValue(glyphTypeface, out var data))
        {
            return base.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);
        }

        // What Avalonia simulates for the face asked for, as FontCollectionBase decides it.
        syntheticGlyphTypeface = null;
        var key = new FontCollectionKey(style, weight, stretch);
        var simulations = FontSimulations.None;
        if (style != FontStyle.Normal && glyphTypeface.Style != style)
        {
            simulations |= FontSimulations.Oblique;
        }

        if (weight >= FontWeight.DemiBold && glyphTypeface.Weight < weight)
        {
            simulations |= FontSimulations.Bold;
        }

        if (simulations == FontSimulations.None
            || new FontCollectionKey(glyphTypeface.Style, glyphTypeface.Weight, glyphTypeface.Stretch) == key)
        {
            return false;
        }

        if (TryGetGlyphTypeface(glyphTypeface.FamilyName, key, allowNearestMatch: false, out var made)
            && made.FontSimulations == simulations)
        {
            syntheticGlyphTypeface = made;
            return true;
        }

        syntheticGlyphTypeface = TryCreateInPlace(data, simulations);
        if (syntheticGlyphTypeface == null)
        {
            return base.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, weight, stretch, out syntheticGlyphTypeface);
        }

        if (!string.IsNullOrEmpty(glyphTypeface.TypographicFamilyName))
        {
            TryAddGlyphTypeface(glyphTypeface.TypographicFamilyName, key, syntheticGlyphTypeface);
        }

        foreach (var familyName in glyphTypeface.FamilyNames)
        {
            TryAddGlyphTypeface(familyName.Value, key, syntheticGlyphTypeface);
        }

        return true;
    }

    private void Add(Assembly assembly, string resource)
    {
        var stopwatch = Stopwatch.StartNew();
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream == null)
        {
            AppLog.Warning("Fonts.Load", $"{resource} is not in the assembly.");
            return;
        }

        if (TryWrapInPlace(stream) is { } data && TryCreateInPlace(data, FontSimulations.None) is { } inPlace
            && TryAddGlyphTypeface(inPlace))
        {
            AppLog.Debug("Fonts.Load", $"{resource}: in place, {stopwatch.ElapsedMilliseconds} ms");
            return;
        }

        stream.Position = 0;
        bool added = TryAddGlyphTypeface(stream, out _);
        AppLog.Information("Fonts.Load", $"{resource}: {(added ? "copied" : "not loaded")}, {stopwatch.ElapsedMilliseconds} ms");
    }

    // The resource's bytes as an SKData that only points at them: they stay where the assembly is mapped, for as long as
    // it is loaded, which is as long as the app runs. No release proc, so Skia copies nothing.
    private static unsafe SKData? TryWrapInPlace(Stream stream) =>
        stream is UnmanagedMemoryStream { PositionPointer: not null } memory
        && SkiaTypefaceConstructor != null
        && memory.Length <= int.MaxValue
            ? SKData.Create((IntPtr)memory.PositionPointer, (int)memory.Length)
            : null;

    // A face over the bytes, with its own typeface, as a face disposes the typeface it was made with.
    private GlyphTypeface? TryCreateInPlace(SKData data, FontSimulations simulations)
    {
        try
        {
            var typeface = SKTypeface.FromData(data);
            if (typeface == null)
            {
                return null;
            }

            var platformTypeface = (IPlatformTypeface)SkiaTypefaceConstructor!.Invoke([typeface, simulations]);
            var glyphTypeface = new GlyphTypeface(platformTypeface, simulations);
            _inPlace[glyphTypeface] = data;
            return glyphTypeface;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Fonts.LoadInPlace", ex);
            return null;
        }
    }
}
