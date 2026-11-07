using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform;
using GimmeCapture.Services.Platforms.Avalonia;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Inter, the Fluent theme's font, is read the first time something draws in it
/// (<see cref="DeferredInterFontCollection"/>) rather than as Avalonia is set up, which read all six faces at every
/// start: about 13 ms and 3 MB. The probe sets up Avalonia, which a process can do only once, so it is opt-in
/// (INTER_FONT_PROBE=1).
/// </summary>
public sealed class DeferredInterFontCollectionTests
{
    private readonly ITestOutputHelper _output;

    public DeferredInterFontCollectionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheSource_HoldsInterInSixWeights()
    {
        // An Avalonia update that moves Inter's faces would leave the collection empty, and text in Inter drawn in the
        // system's font instead, without an error.
        string[] faces = new StandardAssetLoader()
            .GetAssets(new Uri(DeferredInterFontCollection.SourceText), null)
            .Select(asset => Path.GetFileName(asset.AbsolutePath))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Inter-Bold.ttf", "Inter-Light.ttf", "Inter-Medium.ttf", "Inter-Regular.ttf", "Inter-SemiBold.ttf",
                "Inter-Thin.ttf",
            ],
            faces);
    }

    [Fact]
    public void TheApp_NamesNothingOfInterFontsAssembly()
    {
        // WithInterFont, or any type of Avalonia.Fonts.Inter named in the app, would load the assembly at start again,
        // and WithInterFont would read the six faces with it.
        Assert.DoesNotContain(
            typeof(GimmeCapture.App).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Avalonia.Fonts.Inter");
    }

    [OptInFact("INTER_FONT_PROBE=1")]
    public void Inter_LoadsTheSameFacesTheFirstTimeItIsAskedFor()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var collection = new DeferredInterFontCollection();
        FontManager.Current.AddFontCollection(collection);
        Assert.False(collection.IsLoaded);
        Assert.Empty(collection);

        var stopwatch = Stopwatch.StartNew();
        var semiBold = new Typeface(new FontFamily("fonts:Inter#Inter"), weight: FontWeight.SemiBold);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(semiBold, out var first));
        long loadMs = stopwatch.ElapsedMilliseconds;
        Assert.True(collection.IsLoaded);
        Assert.Equal(FontWeight.SemiBold, first.Weight);

        // Each weight comes out as the face Avalonia's own collection gives, synthesis included.
        var eager = new Avalonia.Fonts.Inter.InterFontCollection();
        FontWeight[] weights =
        [
            FontWeight.Thin, FontWeight.Light, FontWeight.Normal, FontWeight.Medium, FontWeight.SemiBold,
            FontWeight.Bold, FontWeight.ExtraBold, FontWeight.Black,
        ];
        foreach (var weight in weights)
        {
            var (style, stretch) = (FontStyle.Normal, FontStretch.Normal);
            Assert.True(collection.TryGetGlyphTypeface("Inter", style, weight, stretch, out var got));
            Assert.True(eager.TryGetGlyphTypeface("Inter", style, weight, stretch, out var expected));
            Assert.Equal(
                (expected.FamilyName, expected.Weight, expected.Style, expected.FontSimulations),
                (got.FamilyName, got.Weight, got.Style, got.FontSimulations));
        }

        // The faces read and those the requests above synthesized, alike in both.
        Assert.True(collection.TryGetFamilyTypefaces("Inter", out var typefaces));
        Assert.True(eager.TryGetFamilyTypefaces("Inter", out var expectedTypefaces));
        Assert.Equal(Describe(expectedTypefaces), Describe(typefaces));
        _output.WriteLine(
            $"Inter read in {loadMs} ms, at the first request; its faces: {string.Join(", ", Describe(typefaces))}.");
    }

    private static string[] Describe(IReadOnlyList<Typeface> typefaces) =>
        typefaces
            .Select(face => $"{face.FontFamily.Name} {face.Style} {(int)face.Weight} {face.Stretch}")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
