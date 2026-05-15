using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GimmeCapture.Composition;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Platforms.Windows;

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
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            _bootstrapper = new AppBootstrapper();
            desktop.MainWindow = _bootstrapper.CreateMainWindow();

            if (desktop.MainWindow is { } mw && StartupService.ShouldLaunchToTrayOnly(Program.CommandLineArgs))
            {
                void OnOpenedForTrayOnly(object? s, EventArgs e)
                {
                    mw.Opened -= OnOpenedForTrayOnly;
                    mw.Hide();
                }

                mw.Opened += OnOpenedForTrayOnly;
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
                        if (desktop.MainWindow != null)
                        {
                            desktop.MainWindow.Show();
                            desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                            desktop.MainWindow.Activate();
                        }
                    };
                    
                    var menu = new Avalonia.Controls.NativeMenu();

                    // Show
                    var showItem = new Avalonia.Controls.NativeMenuItem(loc["TrayShow"] ?? "Show");
                    showItem.Click += (s, e) => 
                    {
                         if (desktop.MainWindow != null)
                        {
                            desktop.MainWindow.Show();
                            desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                            desktop.MainWindow.Activate();
                        }
                    };
                    menu.Items.Add(showItem);

                    // Separator
                    menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());

                    // Auto Start toggle
                    var vm = desktop.MainWindow?.DataContext as MainWindowViewModel;
                    var autoStartItem = new Avalonia.Controls.NativeMenuItem(
                        FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", vm?.RunOnStartup ?? false));
                    autoStartItem.Click += (s, e) =>
                    {
                        if (desktop.MainWindow?.DataContext is MainWindowViewModel mvm)
                        {
                            mvm.RunOnStartup = !mvm.RunOnStartup;
                            // SetStartup and SaveSettings are now completely handled within the ViewModel setter
                        }
                    };
                    menu.Items.Add(autoStartItem);

                    // Auto Check Updates toggle
                    var autoUpdateItem = new Avalonia.Controls.NativeMenuItem(
                        FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", vm?.AutoCheckUpdates ?? false));
                    autoUpdateItem.Click += (s, e) =>
                    {
                        if (desktop.MainWindow?.DataContext is MainWindowViewModel mvm)
                        {
                            mvm.AutoCheckUpdates = !mvm.AutoCheckUpdates;
                        }
                    };
                    menu.Items.Add(autoUpdateItem);

                    if (vm != null)
                    {
                        vm.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName == nameof(MainWindowViewModel.RunOnStartup))
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                                    autoStartItem.Header = FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", vm.RunOnStartup);
                                });
                            }
                            else if (e.PropertyName == nameof(MainWindowViewModel.AutoCheckUpdates))
                            {
                                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                                    autoUpdateItem.Header = FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", vm.AutoCheckUpdates);
                                });
                            }
                        };
                    }

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
                                if (vm != null)
                                {
                                    autoStartItem.Header = FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", vm.RunOnStartup);
                                    autoUpdateItem.Header = FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", vm.AutoCheckUpdates);
                                }
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
}
