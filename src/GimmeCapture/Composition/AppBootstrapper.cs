using GimmeCapture.Services.Abstractions;
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

    public AppBootstrapper()
    {
        _settingsService = new AppSettingsService();
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
    }

    public MainWindow CreateMainWindow()
    {
        return _mainWindow ??= new MainWindow(
            _mainWindowViewModel.Value,
            _downloadWindowService.Value,
            _toastService.Value,
            _snipWindowFactory.Value);
    }

    public Window CreateTrayHostWindow()
    {
        if (_trayHostWindow != null)
        {
            return _trayHostWindow;
        }

        var window = new Window
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
            Content = null,
            DataContext = _mainWindowViewModel.Value
        };

        window.Opened += (_, _) =>
        {
            _mainWindowViewModel.Value.HotkeyService.Initialize(window);
        };

        _trayHostWindow = window;
        return window;
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
            ShowElevatedWindowNoticeAsync().Forget("ElevatedNotice.Show");
    }

    private bool _isElevatedNoticeShowing;

    private async Task ShowElevatedWindowNoticeAsync()
    {
        if (_isElevatedNoticeShowing)
        {
            return;
        }

        var owner = ResolveDialogOwner();
        if (owner == null)
        {
            return;
        }

        _isElevatedNoticeShowing = true;
        try
        {
            var loc = LocalizationService.Instance;
            var result = await ConfirmationDialog.ShowConfirmation(
                owner,
                loc["ElevatedWindowNoticeTitle"],
                loc["ElevatedWindowNoticeMessage"],
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

    private Window? ResolveDialogOwner()
    {
        if (_mainWindow is { IsVisible: true })
        {
            return _mainWindow;
        }

        return _trayHostWindow ?? _mainWindow;
    }

    private void RestartAsAdministrator()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

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
            // User declined the UAC elevation prompt; keep running unelevated.
        }
        catch (Exception ex)
        {
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
