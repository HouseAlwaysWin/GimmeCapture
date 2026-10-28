using System;
using System.IO;
using System.Threading.Tasks;

namespace GimmeCapture.Tests;

// Every launch used to save the settings once though nothing had changed: a notification the auto-save listens to was
// raised after the load (MainWindowViewModelSaveTriggerTests). Whatever asks for a save, one that would write exactly
// what the config already holds now leaves the file alone, while a change, or a config the load migrated, is still
// written.
public sealed class AppSettingsUnchangedSaveTests : IDisposable
{
    private static readonly DateTime LongAgo = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsUnchangedSaveTests() => Directory.CreateDirectory(_dir);

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

    private string ConfigPath => Path.Combine(_dir, "config.json");

    // A config as the app leaves it once it has loaded and saved it (that first load fills in the AI folder a first run
    // leaves blank), loaded by a new service, with the file's time set back so that a rewrite shows.
    private async Task<AppSettingsService> LoadSavedConfigAsync()
    {
        await new AppSettingsService(_dir).SaveAsync();
        var firstLoad = new AppSettingsService(_dir);
        await firstLoad.LoadAsync();
        await firstLoad.SaveAsync();

        var service = new AppSettingsService(_dir);
        await service.LoadAsync();
        File.SetLastWriteTimeUtc(ConfigPath, LongAgo);
        return service;
    }

    [Fact]
    public async Task ASaveThatChangesNothing_LeavesTheConfigAlone()
    {
        var service = await LoadSavedConfigAsync();

        await service.SaveAsync();

        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(ConfigPath));
    }

    [Fact]
    public async Task AChangedSetting_IsWritten()
    {
        var service = await LoadSavedConfigAsync();

        service.Settings.SnipHotkey = "Ctrl+Alt+F1";
        await service.SaveAsync();

        Assert.NotEqual(LongAgo, File.GetLastWriteTimeUtc(ConfigPath));
        var reloaded = new AppSettingsService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("Ctrl+Alt+F1", reloaded.Settings.SnipHotkey);
    }

    [Fact]
    public async Task AConfigTheLoadMigrated_IsWrittenAtTheNextSave()
    {
        File.WriteAllText(ConfigPath, """{ "ConfigVersion": 10, "SelectedTranslationEngine": "Ollama" }""");
        var service = new AppSettingsService(_dir);
        await service.LoadAsync();
        File.SetLastWriteTimeUtc(ConfigPath, LongAgo);

        await service.SaveAsync();

        Assert.NotEqual(LongAgo, File.GetLastWriteTimeUtc(ConfigPath));
        Assert.Contains($"\"ConfigVersion\": {AppSettingsService.CurrentConfigVersion}", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task SavingTheSameSettingsAgain_IsSkipped()
    {
        var service = new AppSettingsService(_dir);
        await service.LoadAsync();
        await service.SaveAsync();
        Assert.True(File.Exists(ConfigPath));
        File.SetLastWriteTimeUtc(ConfigPath, LongAgo);

        await service.SaveAsync();

        Assert.Equal(LongAgo, File.GetLastWriteTimeUtc(ConfigPath));
    }
}
