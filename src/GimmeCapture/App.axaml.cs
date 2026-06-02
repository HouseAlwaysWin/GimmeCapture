using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GimmeCapture.Composition;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using System.Diagnostics;

namespace GimmeCapture;

public partial class App : Application
{
    private AppBootstrapper? _bootstrapper;

    private static string FormatToggleLabel(string label, bool isOn) => isOn ? $"✓ {label}" : $"   {label}";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (UpdateService.TryRedirectToPendingUpdatedInstance(AppVersionInfo.CurrentVersion, currentExePath))
            {
                desktop.Shutdown();
                return;
            }

            var verificationResult = UpdateService.VerifyPendingUpdateOnStartup(AppVersionInfo.CurrentVersion, currentExePath);
            if (verificationResult.HasPendingUpdate && !verificationResult.IsSuccess)
            {
                System.Windows.Forms.MessageBox.Show(
                    $"Update verification failed.{Environment.NewLine}{Environment.NewLine}{verificationResult.FailureMessage}",
                    "Update Verification Failed");
                desktop.Shutdown();
                return;
            }

            _bootstrapper = new AppBootstrapper();
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            var hotkeyHost = _bootstrapper.CreateTrayHostWindow();

            var launchToTrayOnly = StartupService.ShouldLaunchToTrayOnly(Program.CommandLineArgs);
            if (!launchToTrayOnly)
            {
                desktop.MainWindow = _bootstrapper.CreateMainWindow();
                hotkeyHost.Show();
            }
            else
            {
                desktop.MainWindow = hotkeyHost;
            }

            // Setup Tray Icon
            try
            {
                var loc = GimmeCapture.Services.Core.Infrastructure.LocalizationService.Instance;
                var assets = Avalonia.Platform.AssetLoader.Open(new System.Uri("avares://GimmeCapture/Assets/kitsune_icon.png"));
                var icon = new Avalonia.Controls.WindowIcon(assets);

                var trayIcon = new Avalonia.Controls.TrayIcon
                {
                    Icon = icon,
                    ToolTipText = "GimmeCapture (Kitsune Mode)"
                };
                    
                    trayIcon.Clicked += (s, e) =>
                    {
                        var mainWindow = EnsureMainWindow(desktop);
                        mainWindow.Show();
                        mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                        mainWindow.Activate();
                    };
                    
                    var menu = new Avalonia.Controls.NativeMenu();

                    // Show
                    var showItem = new Avalonia.Controls.NativeMenuItem(loc["TrayShow"] ?? "Show");
                    showItem.Click += (s, e) => 
                    {
                        var mainWindow = EnsureMainWindow(desktop);
                        mainWindow.Show();
                        mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                        mainWindow.Activate();
                    };
                    menu.Items.Add(showItem);

                    // Separator
                    menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());

                    // Auto Start toggle
                    var autoStartItem = new Avalonia.Controls.NativeMenuItem(
                        FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", _bootstrapper.RunOnStartup));
                    autoStartItem.Click += (s, e) =>
                    {
                        _bootstrapper.RunOnStartup = !_bootstrapper.RunOnStartup;
                        autoStartItem.Header = FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", _bootstrapper.RunOnStartup);
                    };
                    menu.Items.Add(autoStartItem);

                    // Auto Check Updates toggle
                    var autoUpdateItem = new Avalonia.Controls.NativeMenuItem(
                        FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", _bootstrapper.AutoCheckUpdates));
                    autoUpdateItem.Click += (s, e) =>
                    {
                        _bootstrapper.AutoCheckUpdates = !_bootstrapper.AutoCheckUpdates;
                        autoUpdateItem.Header = FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", _bootstrapper.AutoCheckUpdates);
                    };
                    menu.Items.Add(autoUpdateItem);

                    // Separator
                    menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());

                    // Exit
                    var exitItem = new Avalonia.Controls.NativeMenuItem(loc["TrayExit"] ?? "Exit");
                    exitItem.Click += (s, e) => 
                    {
                        desktop.Shutdown();
                    };
                    menu.Items.Add(exitItem);

                    trayIcon.Menu = menu;

                    loc.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == "Item" || e.PropertyName == "Item[]")
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                                showItem.Header = loc["TrayShow"] ?? "Show";
                                exitItem.Header = loc["TrayExit"] ?? "Exit";
                                autoStartItem.Header = FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", _bootstrapper.RunOnStartup);
                                autoUpdateItem.Header = FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", _bootstrapper.AutoCheckUpdates);
                            });
                        }
                    };
                    
                    var trayIcons = new Avalonia.Controls.TrayIcons();
                    trayIcons.Add(trayIcon);
                    
                    SetValue(Avalonia.Controls.TrayIcon.IconsProperty, trayIcons);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting up tray icon: {ex.Message}");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow EnsureMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var mainWindow = _bootstrapper!.CreateMainWindow();
        if (desktop.MainWindow != mainWindow)
        {
            desktop.MainWindow = mainWindow;
        }

        return mainWindow;
    }
}
