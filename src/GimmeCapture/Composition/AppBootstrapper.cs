using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;
using GimmeCapture.Views.Dialogs;
using Avalonia;
using Avalonia.Controls;
using System;

namespace GimmeCapture.Composition;

public sealed class AppBootstrapper
{
    private readonly AppSettingsService _settingsService;
    private readonly IStartupRegistrationService _startupRegistrationService;
    private readonly IMainWindowSettingsPersistenceService _settingsPersistenceService;
    private readonly Lazy<MainWindowViewModelDependencies> _mainWindowDependencies;
    private readonly Lazy<MainWindowViewModel> _mainWindowViewModel;
    private readonly Lazy<IDownloadWindowService> _downloadWindowService;
    private readonly Lazy<ISnipWindowFactory> _snipWindowFactory;
    private MainWindow? _mainWindow;
    private Window? _trayHostWindow;

    public AppBootstrapper()
    {
        _settingsService = new AppSettingsService();
        _startupRegistrationService = new WindowsStartupRegistrationService();
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
        _snipWindowFactory = new Lazy<ISnipWindowFactory>(RuntimeServiceFactory.CreateSnipWindowFactory);
    }

    public MainWindow CreateMainWindow()
    {
        return _mainWindow ??= new MainWindow(
            _mainWindowViewModel.Value,
            _downloadWindowService.Value,
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
            ShowInTaskbar = false,
            CanResize = false,
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
            _ = _settingsService.SaveAsync();
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
            _ = _settingsService.SaveAsync();
        }
    }

    private void ConfigureSharedViewModel(MainWindowViewModel viewModel)
    {
        viewModel.RequestCaptureAction = mode => _snipWindowFactory.Value.Open(viewModel, mode);
        viewModel.GetActiveSnipViewModelAction = () => _snipWindowFactory.Value.GetActiveViewModel() as SnipWindowViewModel;
        viewModel.RequestElevatedWindowPromptAction = ShowElevatedWindowNotice;
    }

    private bool _isElevatedNoticeShowing;

    private async void ShowElevatedWindowNotice()
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
            System.Diagnostics.Debug.WriteLine($"Error showing elevated-window notice: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Error restarting as administrator: {ex.Message}");
        }
    }
}
