using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private static MainWindowViewModelDependencies CreateDesignDependencies()
    {
        var baseDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Design",
            nameof(MainWindowViewModel));

        // Reuse the REAL factory graph, substituting only the platform services with design-time no-ops. This
        // keeps the ~14-service AI/media/settings wiring in one place (the factory) so design-time and runtime
        // can no longer drift apart.
        return Composition.MainWindowViewModelDependenciesFactory.CreateDefault(
            existingSettingsService: new AppSettingsService(baseDataDirectory),
            existingStartupRegistrationService: new NoOpStartupRegistrationService(),
            windowManager: new NoOpWindowManager(),
            themeResourceService: new NoOpThemeResourceService(),
            hotkeyService: new NoOpGlobalHotkeyService(),
            globalHotkeySettingsCoordinator: new NoOpGlobalHotkeySettingsCoordinator(),
            settingsSaveCoordinatorFactory: new NoOpSettingsSaveCoordinatorFactory());
    }

    private sealed class NoOpWindowManager : IWindowManager
    {
        public Avalonia.Controls.Window? GetMainWindow() => null;
        public Avalonia.Controls.Window? GetActiveWindow() => null;
        public Avalonia.Controls.Window? FindWindowByDataContext(object dataContext) => null;
        public TWindow? FindWindowOfType<TWindow>() where TWindow : Avalonia.Controls.Window => null;
        public TWindow? GetActiveWindowOfType<TWindow>() where TWindow : Avalonia.Controls.Window => null;
        public TViewModel? GetWindowDataContext<TWindow, TViewModel>()
            where TWindow : Avalonia.Controls.Window
            where TViewModel : class => null;
    }

    private sealed class NoOpThemeResourceService : IThemeResourceService
    {
        public void UpdateThemeColors(Avalonia.Media.Color accentColor, Avalonia.Media.Color deepColor)
        {
        }
    }

    private sealed class NoOpGlobalHotkeyService : IGlobalHotkeyService
    {
        public Action<int>? OnHotkeyPressed { get; set; }
        public Action<int, string, int>? OnHotkeyRegistrationFailed { get; set; }
        public Action? OnElevatedWindowFocused { get; set; }
        public void Initialize(Avalonia.Controls.Window window) { }
        public void Register(int id, string hotkey) { }
        public void Unregister(int id) { }
        public void SuspendAll() { }
        public void ResumeAll() { }
        public void Dispose() { }
    }

    private sealed class NoOpGlobalHotkeySettingsCoordinator : IGlobalHotkeySettingsCoordinator
    {
        public void RegisterGlobalHotkey(int id, string hotkey) { }
    }

    private sealed class NoOpStartupRegistrationService : IStartupRegistrationService
    {
        public void SetStartup(bool runOnStartup) { }
        public bool IsRegistered() => false;
        public bool IsDisabledByOs() => false;
    }

    private sealed class NoOpSettingsSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory
    {
        public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync) => new NoOpSettingsSaveCoordinator();
    }

    private sealed class NoOpSettingsSaveCoordinator : ISettingsSaveCoordinator
    {
        public void RequestSave() { }
    }
}
