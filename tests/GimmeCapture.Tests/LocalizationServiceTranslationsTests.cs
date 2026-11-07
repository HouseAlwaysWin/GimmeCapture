using System.Text;
using System.Text.Json;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The locale files are read with a plain JSON reader, only the language in use and only when a string of it is first
/// looked up (<see cref="LocalizationService.ReadTranslations"/>): reading all three through the serializer took about
/// 7 ms of every start's UI thread.
/// </summary>
public class LocalizationServiceTranslationsTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-TW")]
    [InlineData("ja-JP")]
    public void EachLocaleFile_ReadsAsTheSerializerReadIt(string locale)
    {
        string path = Path.Combine(RepositoryFiles.AppSource, "Assets", "Localization", locale + ".json");

        var read = LocalizationService.ReadTranslations(File.ReadAllBytes(path));
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

        Assert.Equal(
            deserialized.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            read.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.True(read.Count > 500, $"{locale} has only {read.Count} strings.");
    }

    [Fact]
    public void ReadTranslations_SkipsAByteOrderMark_AndUnescapesItsStrings()
    {
        // JSON escapes in the file: a character by its code, a new line and quotes.
        byte[] json = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"A": "x\u4e2d\n", "B": "\"q\""}""")];

        var read = LocalizationService.ReadTranslations(json);

        Assert.Equal("x\u4e2d\n", read["A"]);
        Assert.Equal("\"q\"", read["B"]);
    }

    [Theory]
    [InlineData("{\"A\": 1}")]
    [InlineData("{\"A\": {\"B\": \"c\"}}")]
    [InlineData("[\"A\"]")]
    [InlineData("{\"A\": \"b\"} {}")]
    public void ReadTranslations_RejectsAnythingButOneObjectOfStrings(string json)
    {
        Assert.ThrowsAny<JsonException>(() => LocalizationService.ReadTranslations(Encoding.UTF8.GetBytes(json)));
    }
}
