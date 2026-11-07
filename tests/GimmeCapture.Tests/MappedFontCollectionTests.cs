using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Avalonia;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The CJK fonts are read in place from their assembly (<see cref="MappedFontCollection"/>): through avares:// SkiaSharp
/// copied each whole file into native memory, 16.9-17.2 MB of private bytes for Noto Serif TC before a character was
/// drawn (headless with Skia, two runs). The other tests guard what the collection relies on and run everywhere; the
/// probe measures, opt-in (FONT_PROBE=1), as it sets up Avalonia, which a process can do only once.
/// </summary>
public sealed class MappedFontCollectionTests
{
    private const string ChineseText = "截圖 錄影 翻譯 釘選 壓縮 設定 歷史 模組 省記憶體";
    private readonly ITestOutputHelper _output;

    public MappedFontCollectionTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(MappedFontCollection.ChineseResource)]
    [InlineData(MappedFontCollection.JapaneseResource)]
    public unsafe void EachFont_IsAResourceAlreadyInMemory(string resource)
    {
        using var stream = FontsAssembly().GetManifestResourceStream(resource);

        var memory = Assert.IsAssignableFrom<UnmanagedMemoryStream>(stream);
        Assert.True(memory.PositionPointer != null);
        Assert.True(memory.Length > 10 * 1024 * 1024, $"{resource} is only {memory.Length} bytes.");
    }

    [Fact]
    public void TheFonts_AreNotInTheAppsOwnAssembly()
    {
        // In it they made a 40 MB file that every start read whole, though no start draws before its hotkeys work. And
        // nothing in it may name the fonts' assembly, which it loads by name: ReadyToRun fix-ups of such a name would load
        // it at start-up, as an inlined error message box once loaded WinForms at every start.
        var app = typeof(GimmeCapture.App).Assembly;
        string[] resources = app.GetManifestResourceNames();

        Assert.DoesNotContain(MappedFontCollection.ChineseResource, resources);
        Assert.DoesNotContain(MappedFontCollection.JapaneseResource, resources);
        Assert.DoesNotContain(
            app.GetReferencedAssemblies(), reference => reference.Name == MappedFontCollection.FontsAssemblyName);
    }

    [Theory]
    [InlineData(MappedFontCollection.ChineseFamily, new string[0], new[] { MappedFontCollection.ChineseResource })]
    [InlineData("noto serif jp", new[] { MappedFontCollection.ChineseResource }, new[] { MappedFontCollection.JapaneseResource })]
    [InlineData(MappedFontCollection.ChineseFamily, new[] { MappedFontCollection.ChineseResource }, new string[0])]
    // A character matched in any of them, or a family the collection does not know: every font not loaded yet.
    [InlineData(null, new[] { MappedFontCollection.ChineseResource }, new[] { MappedFontCollection.JapaneseResource })]
    [InlineData("Noto Serif TC ExtraLight", new string[0], new[] { MappedFontCollection.ChineseResource, MappedFontCollection.JapaneseResource })]
    [InlineData(null, new[] { MappedFontCollection.ChineseResource, MappedFontCollection.JapaneseResource }, new string[0])]
    public void EachFont_LoadsWhenItsFamilyIsFirstAskedFor(string? family, string[] loaded, string[] expected)
    {
        // Loaded at registration, the two fonts took 23-34 ms of every start before any window drew.
        Assert.Equal(expected, MappedFontCollection.ResourcesToLoad(family, new HashSet<string>(loaded)));
    }

    private static System.Reflection.Assembly FontsAssembly() =>
        System.Reflection.Assembly.Load(MappedFontCollection.FontsAssemblyName);

    [Fact]
    public void AvaloniaSkia_StillWrapsATypefaceTheWayTheCollectionBuildsIt()
    {
        // Without it every font is copied again, silently: an Avalonia update that renames or reshapes the internal
        // Avalonia.Skia.SkiaTypeface lands here.
        Assert.NotNull(MappedFontCollection.SkiaTypefaceConstructor);
    }

    [OptInFact("FONT_PROBE=1")]
    public void TheChineseFont_LoadsWithoutACopy()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        Draw(new FontFamily("avares://GimmeCapture/Assets/Fonts/Cinzel/Cinzel-VariableFont_wght.ttf#Cinzel"));
        long before = PrivateBytes();

        // The font loads here, the first time its family is asked for: the Cinzel text above loads neither font.
        var family = new FontFamily(LocalizationService.ChineseSource);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var glyphTypeface));
        long loaded = PrivateBytes();
        Draw(family);
        long drawn = PrivateBytes();

        // The same font read the way avares:// read it, through a stream, with Avalonia's own synthetic bold.
        using (var stream = FontsAssembly().GetManifestResourceStream(MappedFontCollection.ChineseResource)!)
        {
            FontManager.Current.AddFontCollection(new CopyingFontCollection(stream));
        }

        byte[] inPlace = Draw(family);
        byte[] copied = Draw(new FontFamily("fonts:copied#Noto Serif TC"));
        int differing = inPlace.Zip(copied, (a, b) => a != b).Count(d => d);

        _output.WriteLine(
            $"{glyphTypeface.FamilyName}: private bytes +{Megabytes(loaded - before)} MB to load it, " +
            $"+{Megabytes(drawn - loaded)} MB more to draw it in the app's three weights; " +
            $"{differing} of {inPlace.Length} bytes differ from the font read through a stream.");
        Assert.StartsWith("Noto Serif TC", glyphTypeface.FamilyName, StringComparison.Ordinal);
        Assert.True(loaded - before < 4 * 1024 * 1024, $"Loading the font took {Megabytes(loaded - before)} MB.");
        // The drawing from the load on. The bound was set for the drawing alone (0.4-1.8 MB) when the font was built at
        // registration, before the first figure; loaded at first use, the font's 0.9-1.5 MB moved after it, and
        // drawn - before came to 2.1-4.0 MB, failing at random. A copy costs 17 MB in either step.
        Assert.True(drawn - loaded < 4 * 1024 * 1024, $"Drawing it took {Megabytes(drawn - loaded)} MB.");
        Assert.Equal(copied.Length, inPlace.Length);
        Assert.Equal(0, differing);
    }

    // In the weights the app uses: the variable font's one face is ExtraLight, so SemiBold and Bold are synthesized.
    private static byte[] Draw(FontFamily family)
    {
        var lines = new StackPanel { Background = Brushes.Black };
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
        {
            lines.Children.Add(new TextBlock
            {
                Text = ChineseText, FontFamily = family, FontWeight = weight, FontSize = 18, Foreground = Brushes.White,
            });
        }

        var window = new Window { Content = lines, Width = 800, Height = 160 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        byte[] pixels;
        using (var frame = window.CaptureRenderedFrame()!)
        using (var buffer = frame.Lock())
        {
            pixels = new byte[buffer.RowBytes * buffer.Size.Height];
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address, pixels, 0, pixels.Length);
        }

        window.Close();
        Dispatcher.UIThread.RunJobs();
        return pixels;
    }

    private sealed class CopyingFontCollection : Avalonia.Media.Fonts.FontCollectionBase
    {
        public CopyingFontCollection(Stream stream) => TryAddGlyphTypeface(stream, out _);

        public override Uri Key { get; } = new("fonts:copied");
    }

    private static long PrivateBytes()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024).ToString("0.0");
}
