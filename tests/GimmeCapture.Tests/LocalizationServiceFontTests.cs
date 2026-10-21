using System.Linq;
using Avalonia.Media;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// The gothic buttons' font chain was Cinzel, Japanese, Chinese for every language. Avalonia takes each glyph from the
// first font that has it, and the Japanese font has nearly every Han character, so Chinese labels came out in Japanese
// glyph forms, and a Chinese UI loaded both CJK fonts.
public class LocalizationServiceFontTests
{
    private static string[] Families(Language language) =>
        FontFamily.Parse(LocalizationService.ButtonFontSources(language)).FamilyNames.ToArray();

    [Fact]
    public void Chinese_TakesItsGlyphsFromTheChineseFont()
    {
        Assert.Equal(["Cinzel", "Noto Serif TC"], Families(Language.Chinese));
    }

    [Fact]
    public void Japanese_TakesItsGlyphsFromTheJapaneseFont()
    {
        Assert.Equal(["Cinzel", "Noto Serif JP"], Families(Language.Japanese));
    }

    [Fact]
    public void English_KeepsCinzelFirst_WithBothCjkFontsBehindIt()
    {
        Assert.Equal(["Cinzel", "Noto Serif JP", "Noto Serif TC"], Families(Language.English));
    }
}
