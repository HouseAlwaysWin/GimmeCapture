using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;
using ReactiveUI.Reactive;

namespace GimmeCapture.Tests.ViewModels;

// Once the settings are in, display state the view model raises on its own must not ask for a save: the hardware probe
// raising IsGifAvailable after the load saved the settings at every launch. A persisted change still does.
public sealed class MainWindowViewModelSaveTriggerTests
{
    private static async Task<(MainWindowViewModel ViewModel, CountingSaveCoordinatorFactory Saves)> LoadedViewModelAsync()
    {
        var saves = new CountingSaveCoordinatorFactory();
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsSaveCoordinatorFactory = saves
        });
        await viewModel.InitialSettingsLoadTask;
        return (viewModel, saves);
    }

    [Fact]
    public async Task DisplayStateRaisedOnItsOwn_DoesNotAskForASave()
    {
        var (viewModel, saves) = await LoadedViewModelAsync();
        int before = saves.Requests;

        // What the hardware probe raises when it answers, and the Record tab's lists.
        viewModel.RaisePropertyChanged(nameof(MainWindowViewModel.IsGifAvailable));
        viewModel.RaisePropertyChanged(nameof(MainWindowViewModel.AvailableRecordFormats));
        viewModel.RecordingSettings.RaisePropertyChanged(nameof(RecordingSettingsViewModel.VideoCodecOptions));
        viewModel.RecordingSettings.RaisePropertyChanged(nameof(RecordingSettingsViewModel.MicInputDevices));

        Assert.Equal(before, saves.Requests);
    }

    [Fact]
    public async Task APersistedChange_StillAsksForASave()
    {
        var (viewModel, saves) = await LoadedViewModelAsync();
        int before = saves.Requests;

        viewModel.BorderThickness += 1;
        viewModel.RecordingSettings.RecordFPS += 5;

        Assert.Equal(before + 2, saves.Requests);
    }

    private sealed class CountingSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory, ISettingsSaveCoordinator
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync) => this;

        public void RequestSave() => Interlocked.Increment(ref _requests);
    }
}
