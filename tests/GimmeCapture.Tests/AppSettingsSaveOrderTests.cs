using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Tests;

// A save serializes the settings on the thread that asks for it, the UI thread, which is the one that changes them, and
// does its file work on the thread pool, so a capture never waits for the disk. What a save writes is the settings as they
// stood when it was asked for, and saves asked for one after another without waiting still land in that order.
public sealed class AppSettingsSaveOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsSaveOrderTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task ASave_WritesTheSettingsAsTheyStoodWhenItWasAskedFor()
    {
        var service = new AppSettingsService(_dir);
        service.Settings.Language = Language.Japanese;

        Task save = service.SaveAsync();
        service.Settings.Language = Language.English;
        await save;

        // By name, as the options every load and save share write enums.
        Assert.Contains("\"Language\": \"Japanese\"", File.ReadAllText(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public async Task SavesAskedForInARow_LeaveTheLastOneInTheConfig()
    {
        var service = new AppSettingsService(_dir);
        var saves = new List<Task>();
        for (int i = 1; i <= 20; i++)
        {
            service.Settings.SnipHotkey = $"Ctrl+Alt+{i}";
            saves.Add(service.SaveAsync());
        }

        await Task.WhenAll(saves);

        var reloaded = new AppSettingsService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("Ctrl+Alt+20", reloaded.Settings.SnipHotkey);
    }
}
