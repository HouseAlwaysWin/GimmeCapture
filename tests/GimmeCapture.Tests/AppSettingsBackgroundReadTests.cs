using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Tests;

// Start-up reads the settings on the thread pool from Program.Main (SettingsPreload), so the UI thread only applies them:
// the main view model then applies them as it is built, and the global hotkeys register before the main window is up
// instead of after it, when a press in between was lost. The read must leave the live settings alone, which the UI may
// be reading meanwhile, and the load that applies it must not read the file a second time.
public sealed class AppSettingsBackgroundReadTests : IDisposable
{
    // Generous: the read queues on the thread pool, which the rest of the suite keeps busy.
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsBackgroundReadTests() => Directory.CreateDirectory(_dir);

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

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_dir, "config.json"), json);

    [Fact]
    public async Task TheReadLeavesTheSettingsAlone_AndTheLoadOnlyAppliesIt()
    {
        WriteConfig($$"""{ "ConfigVersion": {{AppSettingsService.CurrentConfigVersion}}, "Language": "Japanese", "SnipHotkey": "Ctrl+Alt+F1" }""");
        var service = new AppSettingsService(_dir);

        service.StartReadingInBackground();
        Assert.True(service.TryGetReadLanguage(ReadWait, out Language language));

        Assert.Equal(Language.Japanese, language);
        Assert.Equal(new AppSettings().SnipHotkey, service.Settings.SnipHotkey);

        Task load = service.LoadAsync();

        Assert.True(load.IsCompletedSuccessfully);
        await load;
        Assert.Equal("Ctrl+Alt+F1", service.Settings.SnipHotkey);
        Assert.Equal(Language.Japanese, service.Settings.Language);
    }

    [Fact]
    public async Task ALaterLoad_DoesNotReadTheFileAgain()
    {
        WriteConfig("""{ "SnipHotkey": "Ctrl+Alt+F1" }""");
        var service = new AppSettingsService(_dir);
        await service.LoadAsync();

        WriteConfig("""{ "SnipHotkey": "Ctrl+Alt+F2" }""");
        await service.LoadAsync();

        Assert.Equal("Ctrl+Alt+F1", service.Settings.SnipHotkey);
    }

    [Fact]
    public void OnAFirstRun_TheReadLanguageIsTheDefault()
    {
        var service = new AppSettingsService(_dir);

        service.StartReadingInBackground();

        Assert.True(service.TryGetReadLanguage(ReadWait, out Language language));
        Assert.Equal(new AppSettings().Language, language);
    }

    [Fact]
    public async Task AnUnreadableConfig_LeavesTheDefaults()
    {
        WriteConfig("not json");
        var service = new AppSettingsService(_dir);
        service.StartReadingInBackground();

        Assert.True(service.TryGetReadLanguage(ReadWait, out Language language));
        await service.LoadAsync();

        Assert.Equal(new AppSettings().Language, language);
        Assert.Equal(new AppSettings().SnipHotkey, service.Settings.SnipHotkey);
    }

    [Fact]
    public void WithNoReadStarted_ThereIsNoLanguageToWaitFor()
    {
        WriteConfig("""{ "Language": "Japanese" }""");

        Assert.False(new AppSettingsService(_dir).TryGetReadLanguage(ReadWait, out _));
    }
}
