using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// The windows are built before the settings load, so start-up reads the saved language on its own first. Without it
// the main window was laid out in English and then again in the saved language.
public sealed class AppSettingsLanguagePeekTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsLanguagePeekTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private Language? Peek(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), json);
        return new AppSettingsService(_dir).PeekSavedLanguage();
    }

    [Fact]
    public void ReadsTheSavedLanguage()
    {
        Assert.Equal(Language.Chinese, Peek("""{ "Language": "Chinese", "SnipHotkey": "Shift+F1" }"""));
    }

    [Fact]
    public void ReadsALanguageSavedAsANumber()
    {
        Assert.Equal(Language.Japanese, Peek("""{ "Language": 2 }"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "Language": "Klingon" }""")]
    [InlineData("not json")]
    public void IsNull_WhenTheConfigHasNoUsableLanguage(string json)
    {
        Assert.Null(Peek(json));
    }

    [Fact]
    public void IsNull_OnAFirstRun()
    {
        Assert.Null(new AppSettingsService(_dir).PeekSavedLanguage());
    }

    [Fact]
    public async Task ReadsTheSameConfigTheLoadDoes()
    {
        var writer = new AppSettingsService(_dir);
        writer.Settings.Language = Language.Japanese;
        await writer.SaveAsync();

        var reader = new AppSettingsService(_dir);
        Assert.Equal(Language.Japanese, reader.PeekSavedLanguage());
        await reader.LoadAsync();
        Assert.Equal(Language.Japanese, reader.Settings.Language);
    }
}
