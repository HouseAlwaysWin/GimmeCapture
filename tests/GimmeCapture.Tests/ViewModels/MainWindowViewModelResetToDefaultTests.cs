using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;
using Moq;

namespace GimmeCapture.Tests.ViewModels;

// Reset Defaults sits beside the video-editing button and nothing undoes it: on 2026-11-09 one click there reset 11
// settings, and nobody noticed until the next install. It asks first now.
public sealed class MainWindowViewModelResetToDefaultTests : IDisposable
{
    private static readonly JsonSerializerOptions SavedFormat = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IStartupRegistrationService> _startup = new();

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public MainWindowViewModelResetToDefaultTests() => Directory.CreateDirectory(_dir);

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
    public async Task SayingNo_LeavesEverySettingAlone()
    {
        var viewModel = await LoadViewModelWithChangedSettingsAsync();
        int asked = 0;
        viewModel.ConfirmResetToDefaultAction = () =>
        {
            asked++;
            return Task.FromResult(false);
        };
        string settings = JsonSerializer.Serialize(viewModel.AppSettingsService.Settings);
        string shown = JsonSerializer.Serialize(viewModel.CreateSettingsSnapshot());
        string file = File.ReadAllText(ConfigPath);

        await viewModel.ResetToDefaultCommand.Execute();

        Assert.Equal(1, asked);
        Assert.Equal(settings, JsonSerializer.Serialize(viewModel.AppSettingsService.Settings));
        Assert.Equal(shown, JsonSerializer.Serialize(viewModel.CreateSettingsSnapshot()));
        Assert.Equal(file, File.ReadAllText(ConfigPath));
        _startup.Verify(x => x.SetStartup(It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SayingYes_ResetsThem()
    {
        var viewModel = await LoadViewModelWithChangedSettingsAsync();
        viewModel.ConfirmResetToDefaultAction = () => Task.FromResult(true);

        await viewModel.ResetToDefaultCommand.Execute();

        AssertAreDefaults(viewModel.AppSettingsService.Settings);
        AssertAreDefaults(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath), SavedFormat)!);
        Assert.False(viewModel.RunOnStartup);
        Assert.False(viewModel.SoftwareRendering);
        _startup.Verify(x => x.SetStartup(false), Times.Once);
    }

    [Fact]
    public async Task WithNoOneToAsk_NothingIsReset()
    {
        var viewModel = await LoadViewModelWithChangedSettingsAsync();
        string settings = JsonSerializer.Serialize(viewModel.AppSettingsService.Settings);

        await viewModel.ResetToDefaultCommand.Execute();

        Assert.Equal(settings, JsonSerializer.Serialize(viewModel.AppSettingsService.Settings));
    }

    // Settings of each kind the reset reaches: those the view model holds, those its recording section holds, and those it
    // reads straight from the settings. The first three are among the ones that click cost.
    private async Task<MainWindowViewModel> LoadViewModelWithChangedSettingsAsync()
    {
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            RunOnStartup = true,
            SoftwareRendering = true,
            AIResourcesDirectory = Path.Combine(_dir, "models"),
            AutoCheckUpdates = true,
            BorderThickness = 7.0,
            ThemeColorHex = "#00FF00",
            SnipHotkey = "Ctrl+F1",
            RecordFormat = "mkv",
            EnableWebcam = true,
            Snip = new { Rectangle = "Ctrl+R" }
        }));

        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            SettingsService = new AppSettingsService(_dir),
            StartupRegistrationService = _startup.Object
        });
        await viewModel.InitialSettingsLoadTask;

        Assert.True(viewModel.RunOnStartup);
        Assert.True(viewModel.AppSettingsService.Settings.SoftwareRendering);
        _startup.Invocations.Clear(); // the load registered it
        return viewModel;
    }

    private static void AssertAreDefaults(AppSettings actual)
    {
        var defaults = new AppSettings();
        Assert.Equal(defaults.RunOnStartup, actual.RunOnStartup);
        Assert.Equal(defaults.SoftwareRendering, actual.SoftwareRendering);
        // No folder of its own any more: the reset's save writes out the shared one, which an empty setting stands for.
        Assert.Equal(
            AppStoragePaths.GetSharedAIResourcesDirectory(RuntimePathProvider.GetExecutableDirectory()),
            actual.AIResourcesDirectory);
        Assert.Equal(defaults.AutoCheckUpdates, actual.AutoCheckUpdates);
        Assert.Equal(defaults.BorderThickness, actual.BorderThickness);
        Assert.Equal(Color.Parse(defaults.ThemeColorHex), Color.Parse(actual.ThemeColorHex)); // saved with its alpha
        Assert.Equal(defaults.SnipHotkey, actual.SnipHotkey);
        Assert.Equal(defaults.RecordFormat, actual.RecordFormat);
        Assert.Equal(defaults.EnableWebcam, actual.EnableWebcam);
        Assert.Equal(defaults.Snip.Rectangle, actual.Snip.Rectangle);
    }
}
