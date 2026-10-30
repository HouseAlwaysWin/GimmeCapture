using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Desktop;
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
    private readonly bool _savedRunAsAdministrator;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly IMainWindowSettingsPersistenceService _settingsPersistenceService;
    private readonly Lazy<MainWindowViewModelDependencies> _mainWindowDependencies;
    private readonly Lazy<MainWindowViewModel> _mainWindowViewModel;
    private readonly Lazy<IDownloadWindowService> _downloadWindowService;
    private readonly Lazy<IToastService> _toastService;
    private readonly Lazy<ISnipWindowFactory> _snipWindowFactory;
    // Lets go of the main window once it has sat hidden in the tray for the app's idle delay (ReleaseHiddenMainWindow).
    private readonly IdleReleaseScheduler _mainWindowRelease;
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

        // For TryHandOverToElevatedCopy, from the read just waited for. Deciding in Main, before Avalonia, would take a
        // config read of its own: about 7 ms of every launch, measured cold.
        if (!settingsService.TryGetReadRunAsAdministrator(TimeSpan.Zero, out _savedRunAsAdministrator))
        {
            _savedRunAsAdministrator = settingsService.PeekSavedRunAsAdministrator();
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
        _mainWindowRelease = new IdleReleaseScheduler(
            ProcessMemoryTrimService.IdleTrimDelay,
            () => Avalonia.Threading.Dispatcher.UIThread.Post(ReleaseHiddenMainWindow));

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
        if (_mainWindow == null)
        {
            var window = new MainWindow(_mainWindowViewModel.Value, _downloadWindowService.Value);
            // Closed to the tray, it is let go of after the idle delay; opened again before then, it is kept.
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property != Visual.IsVisibleProperty)
                {
                    return;
                }

                if (window.IsVisible)
                {
                    _mainWindowRelease.Cancel();
                }
                else
                {
                    _mainWindowRelease.NotifyUse();
                }
            };
            _mainWindow = window;
        }

        return _mainWindow;
    }

    /// <summary>
    /// Raised on the UI thread once the main window has been let go of. The lifetime still names it as its main window,
    /// which keeps it, and the clipboard and dialogs go through that: the tray host has to stand in again.
    /// </summary>
    public event Action? MainWindowReleased;

    /// <summary>
    /// Lets go of the main window when it is still hidden in the tray (<see cref="MainWindow.Release"/>). The app is left
    /// as a launch to the tray leaves it, and the next open builds the window again. With every tab opened, that brought
    /// the commit size down 14-28 MB, and the full trim that follows 14 MB more; the rest of what opening it cost stays
    /// (the GPU's share, fonts, loaded libraries). Not while a module download reports through the window, or a Compress
    /// editor confirms through it, which would then confirm unasked: it tries again after another idle delay.
    /// </summary>
    internal void ReleaseHiddenMainWindow()
    {
        if (_mainWindow is not { IsVisible: false } window)
        {
            return;
        }

        if (_mainWindowViewModel.Value.IsProcessing
            || _mainWindowDependencies.Value.WindowManager.FindWindowOfType<VideoEditWindow>() != null)
        {
            _mainWindowRelease.NotifyUse();
            return;
        }

        _mainWindow = null;
        window.Release();
        AppLog.Information("MainWindow.Released", "hidden in the tray for the idle delay");
        MainWindowReleased?.Invoke();
        // Nothing allocates in an idle app, so nothing would collect the window for a long time: the full trim does.
        ProcessMemoryTrimService.RequestIdleTrimAsync("main-window-released").Forget("MemoryTrim.MainWindowReleased");
    }

    /// <summary>
    /// Starts the global hotkey service; the combos the settings registered take effect here. Call it after the main window
    /// is built, when there is one: its handler is what reports a combo another app already holds.
    /// </summary>
    public void InitializeHotkeys() => _mainWindowViewModel.Value.HotkeyService.Initialize();

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
        viewModel.RequestElevatedWindowPromptAction = () =>
            ShowElevatedWindowNoticeAsync(viewModel).Forget("ElevatedNotice.Show");
        viewModel.RequestRestartAsAdministratorAction = () =>
            OfferRestartAsAdministratorAsync(viewModel).Forget("Elevation.OfferRestart");

        // Toasts and the capture-delay countdown are windows of their own, so they are wired here, with the view model.
        // The main window wired them, and a launch to the tray builds it only once the user opens it: until then no copy
        // was confirmed, no error was shown and a delayed capture counted down with nothing on screen.
        viewModel.ShowToastAction = (message, severity) => _toastService.Value.Show(message, severity);
        viewModel.ShowPreviewToastAction = (message, severity, preview) => _toastService.Value.Show(message, severity, preview);
        var captureCountdown = new AvaloniaCaptureCountdownService(viewModel.CancelCaptureCountdown);
        viewModel.ShowCaptureCountdownAction = captureCountdown.ShowAsync;
        viewModel.CloseCaptureCountdownAction = captureCountdown.Close;
    }

    /// <summary>
    /// "Run as administrator" (<see cref="ElevatedLaunch"/>): asks UAC for an elevated copy and hands it the instance
    /// guard, before any window, tray icon or hotkey exists. True when this process is to end: the copy started, or the
    /// guard went to another launch while the prompt was up. A launch with the setting off pays nothing for it.
    /// </summary>
    public bool TryHandOverToElevatedCopy()
    {
        if (!_savedRunAsAdministrator)
        {
            return false;
        }

        var rights = ElevatedLaunch.Current;
        if (!ElevatedLaunch.ShouldRelaunchAtStart(_savedRunAsAdministrator, rights, Program.CommandLineArgs))
        {
            if (rights == AdministratorRights.CanElevate)
            {
                AppLog.Warning(
                    "Elevation.RelaunchAtStart",
                    "This is the copy UAC was asked to elevate, and it is still not elevated; running without administrator rights.");
            }

            return false;
        }

        Program.ReleaseSingleInstanceForRestart();
        if (ElevatedLaunch.TryStartElevatedCopy(Program.CommandLineArgs))
        {
            AppLog.Information("Elevation.RelaunchAtStart: handed over to the elevated copy.");
            return true;
        }

        Program.ReacquireSingleInstanceAfterFailedRestart();
        if (Program.SingleInstance == null)
        {
            SingleInstanceGuard.SignalRunningInstance();
            return true;
        }

        return false;
    }

    private bool _isElevationDialogShowing;

    // The one-time explanation (MainWindowViewModel.OfferRunAsAdministratorOnce), raised while an administrator window has
    // the foreground: Yes turns the setting on and restarts elevated.
    private async Task ShowElevatedWindowNoticeAsync(MainWindowViewModel viewModel)
    {
        var loc = LocalizationService.Instance;
        if (await ConfirmElevationAsync(loc["ElevatedWindowNoticeTitle"], loc["ElevatedWindowNoticeMessage"], topmost: true))
        {
            viewModel.SetRunAsAdministrator(true);
            await RestartAsAdministratorAsync(viewModel);
        }
    }

    // The setting was just turned on: restart now, or leave it to the next launch.
    private async Task OfferRestartAsAdministratorAsync(MainWindowViewModel viewModel)
    {
        if (viewModel.IsCaptureInProgress)
        {
            return;
        }

        var loc = LocalizationService.Instance;
        if (await ConfirmElevationAsync(loc["RunAsAdministrator"], loc["RunAsAdministratorRestartMessage"], topmost: false))
        {
            await RestartAsAdministratorAsync(viewModel);
        }
    }

    private async Task<bool> ConfirmElevationAsync(string title, string message, bool topmost)
    {
        if (_isElevationDialogShowing)
        {
            return false;
        }

        _isElevationDialogShowing = true;
        try
        {
            var result = await ConfirmationDialog.ShowConfirmation(
                ResolveDialogOwner(),
                title,
                message,
                ConfirmationMode.YesNo,
                WindowStartupLocation.CenterScreen,
                topmost);
            return result == ConfirmationResult.Yes;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Elevation.Confirm", ex);
            return false;
        }
        finally
        {
            _isElevationDialogShowing = false;
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

    private async Task RestartAsAdministratorAsync(MainWindowViewModel viewModel)
    {
        // Restarting would end the capture or recording; the setting still applies from the next launch.
        if (viewModel.IsCaptureInProgress)
        {
            return;
        }

        // The elevated copy reads the config as it starts, so what was just turned on has to be on disk by then.
        await viewModel.SaveSettingsAsync();

        // Let go of the single-instance mutex first. The elevated copy acquires it as it starts, while this
        // process is still tearing down — if it loses that race it decides it is a duplicate and exits, so the
        // user would consent to UAC and watch the app disappear instead of come back elevated.
        Program.ReleaseSingleInstanceForRestart();

        if (ElevatedLaunch.TryStartElevatedCopy([]))
        {
            AppLog.Information("Elevation.Restart: handed over to the elevated copy.");
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }

            return;
        }

        // Declined (or it could not start): keep running unelevated, and take the guard back, or this instance would
        // stop being the one a later duplicate launch hands off to.
        Program.ReacquireSingleInstanceAfterFailedRestart();
    }

    public async ValueTask DisposeAsync()
    {
        // The snip window factory owns the translation model, which outlives every overlay.
        if (_snipWindowFactory.IsValueCreated && _snipWindowFactory.Value is IDisposable snipWindowFactory)
        {
            snipWindowFactory.Dispose();
        }

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
