using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// A capture hotkey saves the settings, and the overlay used to wait for that save: it ran on the UI thread up to its first
// real wait, which with the config cached meant the snapshot, the serialize and the read that leaves an unchanged file
// alone. The overlay is now asked for first, and the save runs once the UI thread is idle again.
public sealed class MainWindowViewModelCaptureSaveTests
{
    [Theory]
    [InlineData(CaptureMode.Normal)]
    [InlineData(CaptureMode.Translate)]
    [InlineData(CaptureMode.ScrollingCapture)]
    public async Task TheOverlayIsAskedFor_BeforeTheSaveStarts(CaptureMode mode)
    {
        var events = new List<string>();
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsPersistenceService = new RecordingPersistence(events)
        });
        await viewModel.InitialSettingsLoadTask;
        var idleWork = new List<Action>();
        viewModel.PostWhenIdle = idleWork.Add;
        viewModel.RequestCaptureAction = _ => events.Add("overlay");

        await viewModel.StartCaptureCommand.Execute(mode);

        Assert.Equal(["overlay"], events);
        Action save = Assert.Single(idleWork);
        save();
        Assert.Equal(["overlay", "save"], events);
    }

    // Loads the settings the service holds and writes nothing, noting each save.
    private sealed class RecordingPersistence(List<string> events) : IMainWindowSettingsPersistenceService
    {
        public Task<MainWindowSettingsSnapshot> LoadAsync(IAppSettingsService settingsService) =>
            Task.FromResult(MainWindowSettingsSnapshot.FromAppSettings(settingsService.Settings));

        public Task SaveAsync(IAppSettingsService settingsService, MainWindowSettingsSnapshot snapshot)
        {
            events.Add("save");
            return Task.CompletedTask;
        }
    }
}
