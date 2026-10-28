using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;
using GimmeCapture.Views.Dialogs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Threading.Tasks;

namespace GimmeCapture.Composition;

public sealed class AppBootstrapper : IAsyncDisposable
{
    private readonly IAppSettingsService _settingsService;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly IMainWindowSettingsPersistenceService _settingsPersistenceService;
    private readonly Lazy<MainWindowViewModelDependencies> _mainWindowDependencies;
    private readonly Lazy<MainWindowViewModel> _mainWindowViewModel;
    private readonly Lazy<IDownloadWindowService> _downloadWindowService;
    private readonly Lazy<IToastService> _toastService;
    private readonly Lazy<ISnipWindowFactory> _snipWindowFactory;
    private MainWindow? _mainWindow;
    private Window? _trayHostWindow;

    // How long start-up waits for the settings read Main began (SettingsPreload). It has nearly always finished by now;
    // one still going after this (a first launch moving an old AI folder, say) is applied once it is done.
    private static readonly TimeSpan SettingsReadWait = TimeSpan.FromSeconds(1);

    public AppBootstrapper()
    {
        var preloadedSettings = SettingsPreload.Take();
        var settingsService = preloadedSettings ?? new AppSettingsService();
        // Build the windows in the saved language from the start. Switching it once the settings have loaded lays
        // the whole main window out a second time, and loads the CJK font in the middle of start-up. With the read
        // done, the main view model also applies the rest of the settings as it is built, before any window.
        if (settingsService.TryGetReadLanguage(SettingsReadWait, out Language readLanguage))
        {
            LocalizationService.Instance.CurrentLanguage = readLanguage;
        }
        else
        {
            if (preloadedSettings != null)
            {
                AppLog.Information("Startup.SettingsRead", "Not finished in time; the settings are applied once it is.");
            }

            if (settingsService.PeekSavedLanguage() is { } savedLanguage)
            {
                LocalizationService.Instance.CurrentLanguage = savedLanguage;
            }
        }

        _settingsService = settingsService;
        _startupRegistrationService = MainWindowViewModelDependenciesFactory.CreateStartupRegistrationService();
        _settingsPersistenceService = new MainWindowSettingsPersistenceService();
        _mainWindowDependencies = new Lazy<MainWindowViewModelDependencies>(() =>
            MainWindowViewModelDependenciesFactory.CreateDefault(
                _settingsService,
                _startupRegistrationService,
                _settingsPersistenceService));
        _mainWindowViewModel = new Lazy<MainWindowViewModel>(() =>
        {
            var viewModel = new MainWindowViewModel(_mainWindowDependencies.Value);
            ConfigureSharedViewModel(viewModel);
            return viewModel;
        });
        _downloadWindowService = new Lazy<IDownloadWindowService>(RuntimeServiceFactory.CreateDownloadWindowService);
        _toastService = new Lazy<IToastService>(RuntimeServiceFactory.CreateToastService);
        _snipWindowFactory = new Lazy<ISnipWindowFactory>(RuntimeServiceFactory.CreateSnipWindowFactory);

        // The crash-free half of a failing command is registered with ReactiveUI before any window exists
        // (UnhandledCommandExceptionReporter); this adds the half the user sees, now that toasts can be shown.
        UnhandledCommandExceptionReporter.Shared.AttachUserNotification(_ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _toastService.Value.Show(
                        LocalizationService.Instance["CommandFailedToast"],
                        MainWindowViewModel.ToastSeverity.Error);
                }
                catch (Exception toastFailure)
                {
                    AppLog.Warning("Command.UnhandledToast", toastFailure);
                }
            }));
    }

    /// <summary>The main view model's first settings load: the saved settings are applied once it completes.</summary>
    internal Task InitialSettingsLoadTask => _mainWindowViewModel.Value.InitialSettingsLoadTask;

    /// <summary>The AI resources folder the settings use. Only meaningful once <see cref="InitialSettingsLoadTask"/> is done.</summary>
    internal string GetAIResourcesDirectoryInUse() => new AIPathService(_settingsService).GetAIResourcesPath();

    /// <summary>
    /// Deletes the recordings earlier runs left unsaved in the temp folders (<see cref="RecordingTempCleaner"/>), and
    /// the history entries pointing at them. Call off the UI thread, once <see cref="InitialSettingsLoadTask"/> is
    /// done: the settings name those folders. Never throws.
    /// </summary>
    internal async Task SweepLeftoverRecordingsAsync()
    {
        try
        {
            var removed = RecordingTempCleaner.Sweep(_settingsService.BaseDataDirectory, _settingsService.Settings.TempDirectory);
            if (removed.Count > 0)
            {
                await _mainWindowViewModel.Value.CaptureHistory.ForgetAsync(removed).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("RecordingTemp.Sweep", ex);
        }
    }

    /// <summary>
    /// Builds the main view model. With Main's read of the settings done, it applies them as it is built, which registers
    /// the global hotkeys' combos with the hotkey service for <see cref="InitializeHotkeys"/> to put into effect.
    /// </summary>
    public MainWindowViewModel CreateMainViewModel() => _mainWindowViewModel.Value;

    public MainWindow CreateMainWindow()
    {
        return _mainWindow ??= new MainWindow(
            _mainWindowViewModel.Value,
            _downloadWindowService.Value,
            _toastService.Value,
            _snipWindowFactory.Value);
    }

    /// <summary>
    /// Starts the global hotkey service; the combos the settings registered take effect here. Call it after the main window
    /// is built, when there is one: its handler is what reports a combo another app already holds.
    /// </summary>
    public void InitializeHotkeys() => _mainWindowViewModel.Value.HotkeyService.Initialize();

    /// <summary>Starts the hardware encoder probe (<see cref="MainWindowViewModel.StartHardwareProbe"/>).</summary>
    public void StartHardwareProbe() => _mainWindowViewModel.Value.StartHardwareProbe();

    /// <summary>
    /// Loads the capture overlay's code while the app is idle (<see cref="ISnipWindowFactory.ScheduleWarmUp"/>), ten
    /// seconds in so it stays clear of startup's own work.
    /// </summary>
    public void ScheduleCaptureOverlayWarmUp() => _snipWindowFactory.Value.ScheduleWarmUp(TimeSpan.FromSeconds(10));

    /// <summary>
    /// A window nobody sees (1×1, transparent, off-screen) that gives the app a visible top level while the main window is
    /// not: the lifetime's main window in a launch to the tray, which the clipboard and other top-level lookups use, and
    /// the owner of a dialog shown while the main window is hidden — Avalonia shows no dialog over a hidden owner. It no
    /// longer starts the hotkey service, so a window launch builds it only once a dialog needs it.
    /// </summary>
    public Window CreateTrayHostWindow()
    {
        return _trayHostWindow ??= new Window
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000),
            Content = null
        };
    }

    public bool RunOnStartup
    {
        get => _settingsService.Settings.RunOnStartup;
        set
        {
            if (_mainWindowViewModel.IsValueCreated)
            {
                if (_mainWindowViewModel.Value.RunOnStartup == value)
                {
                    return;
                }

                _mainWindowViewModel.Value.RunOnStartup = value;
                return;
            }

            if (_settingsService.Settings.RunOnStartup == value)
            {
                return;
            }

            _settingsService.Settings.RunOnStartup = value;
            _startupRegistrationService.SetStartup(value);
            _settingsService.SaveAsync().Forget("Settings.SaveRunOnStartup");
        }
    }

    public bool AutoCheckUpdates
    {
        get => _settingsService.Settings.AutoCheckUpdates;
        set
        {
            if (_mainWindowViewModel.IsValueCreated)
            {
                if (_mainWindowViewModel.Value.AutoCheckUpdates == value)
                {
                    return;
                }

                _mainWindowViewModel.Value.AutoCheckUpdates = value;
                return;
            }

            if (_settingsService.Settings.AutoCheckUpdates == value)
            {
                return;
            }

            _settingsService.Settings.AutoCheckUpdates = value;
            _settingsService.SaveAsync().Forget("Settings.SaveAutoCheckUpdates");
        }
    }

    private void ConfigureSharedViewModel(MainWindowViewModel viewModel)
    {
        viewModel.RequestCaptureAction = mode => _snipWindowFactory.Value.Open(viewModel, mode);
        viewModel.GetActiveSnipViewModelAction = () => _snipWindowFactory.Value.GetActiveViewModel() as SnipWindowViewModel;
        viewModel.RequestElevatedWindowPromptAction = blockedHotkey =>
            ShowElevatedWindowNoticeAsync(blockedHotkey).Forget("ElevatedNotice.Show");
    }

    private bool _isElevatedNoticeShowing;

    private async Task ShowElevatedWindowNoticeAsync(string blockedHotkey)
    {
        if (_isElevatedNoticeShowing)
        {
            return;
        }

        _isElevatedNoticeShowing = true;
        try
        {
            var loc = LocalizationService.Instance;
            var result = await ConfirmationDialog.ShowConfirmation(
                ResolveDialogOwner(),
                loc["ElevatedWindowNoticeTitle"],
                string.Format(loc["ElevatedWindowNoticeMessage"], blockedHotkey),
                ConfirmationMode.YesNo,
                WindowStartupLocation.CenterScreen);

            if (result == ConfirmationResult.Yes)
            {
                RestartAsAdministrator();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ElevatedNotice.Show", ex);
        }
        finally
        {
            _isElevatedNoticeShowing = false;
        }
    }

    private Window ResolveDialogOwner()
    {
        if (_mainWindow is { IsVisible: true })
        {
            return _mainWindow;
        }

        var host = CreateTrayHostWindow();
        if (!host.IsVisible)
        {
            host.Show();
        }

        return host;
    }

    private void RestartAsAdministrator()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        // Let go of the single-instance mutex first. The elevated copy acquires it as it starts, while this
        // process is still tearing down — if it loses that race it decides it is a duplicate and exits, so the
        // user would consent to UAC and watch the app disappear instead of come back elevated.
        Program.ReleaseSingleInstanceForRestart();

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(startInfo);

            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User declined the UAC elevation prompt; keep running unelevated — and take the guard back, or this
            // instance would stop being the one a later duplicate launch hands off to.
            Program.ReacquireSingleInstanceAfterFailedRestart();
        }
        catch (Exception ex)
        {
            Program.ReacquireSingleInstanceAfterFailedRestart();
            AppLog.Warning("ElevatedNotice.RestartAsAdministrator", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_mainWindowDependencies.IsValueCreated)
        {
            return;
        }

        var dependencies = _mainWindowDependencies.Value;
        if (dependencies.OcrRuntimeService.IsValueCreated)
        {
            dependencies.OcrRuntimeService.Value.Dispose();
        }

        if (dependencies.SAM2RuntimeService.IsValueCreated)
        {
            dependencies.SAM2RuntimeService.Value.Dispose();
        }

        if (dependencies.HotkeyService is IDisposable disposableHotkeyService)
        {
            disposableHotkeyService.Dispose();
        }

        await dependencies.ResourceQueue.DisposeAsync().ConfigureAwait(false);
    }
}
