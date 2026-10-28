using System;
using System.Collections.Generic;
using System.IO;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// With the settings already read (Program.Main starts that read: SettingsPreload), the main view model applies them as it
// is built, so the global hotkeys' combos reach the hotkey service before any window exists. They used to wait for a load
// that finished after the main window was up, and a hotkey pressed before then was lost.
public sealed class MainWindowViewModelPreloadedSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public MainWindowViewModelPreloadedSettingsTests() => Directory.CreateDirectory(_dir);

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
    public void SettingsAlreadyRead_AreApplied_HotkeysIncluded_WhileTheViewModelIsBuilt()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{ "SnipHotkey": "Ctrl+Alt+F1", "BorderThickness": 5 }""");
        var settings = new AppSettingsService(_dir);
        settings.StartReadingInBackground();
        Assert.True(settings.TryGetReadLanguage(TimeSpan.FromSeconds(30), out _));
        var hotkeys = new RecordingHotkeyCoordinator();

        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsService = settings,
            GlobalHotkeySettingsCoordinator = hotkeys
        });

        Assert.True(viewModel.InitialSettingsLoadTask.IsCompletedSuccessfully);
        Assert.Equal("Ctrl+Alt+F1", viewModel.SnipHotkey);
        Assert.Equal(5, viewModel.BorderThickness);
        Assert.Contains((HotkeyIds.Snip, "Ctrl+Alt+F1"), hotkeys.Registered);
    }

    private sealed class RecordingHotkeyCoordinator : IGlobalHotkeySettingsCoordinator
    {
        public List<(int Id, string Hotkey)> Registered { get; } = new();

        public void RegisterGlobalHotkey(int id, string hotkey) => Registered.Add((id, hotkey));
    }
}
