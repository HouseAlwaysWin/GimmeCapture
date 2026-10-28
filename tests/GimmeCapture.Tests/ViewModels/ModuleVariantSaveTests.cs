using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// Picking a SAM2 or Llama variant on the Modules tab saves the settings. The pick ran in an async void subscriber: a
// second pick started saving while the first still was, so the first could be the one left on disk, and a failure
// escaped to the dispatcher, which ends the process.
public sealed class ModuleVariantSaveTests
{
    [Fact]
    public async Task VariantPicks_SaveOneAtATime_InTheOrderTheyWereMade()
    {
        var persistence = new HeldSettingsPersistence();
        var viewModel = new MainWindowViewModel(
            MainWindowViewModel.CreateDesignDependencies() with { SettingsPersistenceService = persistence });
        await viewModel.InitialSettingsLoadTask;

        // Opening the tab saves each list's current pick, and those saves are held.
        viewModel.EnsureModulesInitialized();
        int savesOnOpen = persistence.SavesStarted;
        Assert.True(savesOnOpen > 0);

        var sam2 = viewModel.Modules.Single(m => m.Name == "SAM2 Model");
        string[] picks = Enum.GetNames<SAM2Variant>().Where(name => name != sam2.SelectedVariant).Take(2).ToArray();
        sam2.SelectedVariant = picks[0];
        sam2.SelectedVariant = picks[1];

        // Queued behind the SAM2 save still running rather than started alongside it.
        Assert.Equal(savesOnOpen, persistence.SavesStarted);

        persistence.ReleaseAll();
        await persistence.WaitForSavesStartedAsync(savesOnOpen + 2);

        Assert.Equal(picks.Select(Enum.Parse<SAM2Variant>), persistence.Sam2VariantsSaved.Skip(savesOnOpen));
        Assert.Equal(Enum.Parse<SAM2Variant>(picks[1]), viewModel.AppSettingsService.Settings.SelectedSAM2Variant);
    }

    /// <summary>
    /// Records the SAM2 variant each save would write, without writing it (the design-time settings folder is shared),
    /// and holds every save until <see cref="ReleaseAll"/>.
    /// </summary>
    private sealed class HeldSettingsPersistence : IMainWindowSettingsPersistenceService
    {
        private readonly MainWindowSettingsPersistenceService _real = new();
        private readonly object _gate = new();
        private readonly List<SAM2Variant> _sam2VariantsSaved = new();
        private readonly List<TaskCompletionSource> _held = new();
        private bool _holding = true;

        public int SavesStarted
        {
            get
            {
                lock (_gate)
                {
                    return _sam2VariantsSaved.Count;
                }
            }
        }

        public IReadOnlyList<SAM2Variant> Sam2VariantsSaved
        {
            get
            {
                lock (_gate)
                {
                    return _sam2VariantsSaved.ToArray();
                }
            }
        }

        public Task<MainWindowSettingsSnapshot> LoadAsync(IAppSettingsService settingsService) =>
            _real.LoadAsync(settingsService);

        public Task SaveAsync(IAppSettingsService settingsService, MainWindowSettingsSnapshot snapshot)
        {
            lock (_gate)
            {
                _sam2VariantsSaved.Add(settingsService.Settings.SelectedSAM2Variant);
                if (!_holding)
                {
                    return Task.CompletedTask;
                }

                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _held.Add(held);
                return held.Task;
            }
        }

        public void ReleaseAll()
        {
            TaskCompletionSource[] held;
            lock (_gate)
            {
                _holding = false;
                held = _held.ToArray();
                _held.Clear();
            }

            foreach (var save in held)
            {
                save.SetResult();
            }
        }

        public async Task WaitForSavesStartedAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (SavesStarted < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"{SavesStarted} of {count} saves started.");
                }

                await Task.Delay(10);
            }
        }
    }
}
