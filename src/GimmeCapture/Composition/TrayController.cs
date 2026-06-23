using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Composition;

/// <summary>
/// Owns the system tray icon, its native menu, click handling, and localization sync.
/// Extracted verbatim from App.axaml.cs (App Shell / Tray orchestration) — no behavior change.
/// </summary>
public sealed class TrayController
{
    private readonly Application _app;
    private readonly AppBootstrapper _bootstrapper;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    // Held so the localization change handler can update them for the tray's lifetime.
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _autoStartItem;
    private NativeMenuItem? _autoUpdateItem;
    private NativeMenuItem? _exitItem;

    public TrayController(Application app, AppBootstrapper bootstrapper, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        _bootstrapper = bootstrapper;
        _desktop = desktop;
    }

    private static string FormatToggleLabel(string label, bool isOn) => isOn ? $"✓ {label}" : $"   {label}";

    public void Install()
    {
        try
        {
            var loc = LocalizationService.Instance;
            var assets = Avalonia.Platform.AssetLoader.Open(new Uri("avares://GimmeCapture/Assets/kitsune_icon.png"));
            var icon = new WindowIcon(assets);

            var trayIcon = new TrayIcon
            {
                Icon = icon,
                ToolTipText = "GimmeCapture (Kitsune Mode)"
            };

            trayIcon.Clicked += (s, e) => ShowMainWindow();

            var menu = new NativeMenu();

            // Show
            _showItem = new NativeMenuItem(loc["TrayShow"] ?? "Show");
            _showItem.Click += (s, e) => ShowMainWindow();
            menu.Items.Add(_showItem);

            // Separator
            menu.Items.Add(new NativeMenuItemSeparator());

            // Auto Start toggle
            _autoStartItem = new NativeMenuItem(
                FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", _bootstrapper.RunOnStartup));
            _autoStartItem.Click += (s, e) =>
            {
                _bootstrapper.RunOnStartup = !_bootstrapper.RunOnStartup;
                _autoStartItem.Header = FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", _bootstrapper.RunOnStartup);
            };
            menu.Items.Add(_autoStartItem);

            // Auto Check Updates toggle
            _autoUpdateItem = new NativeMenuItem(
                FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", _bootstrapper.AutoCheckUpdates));
            _autoUpdateItem.Click += (s, e) =>
            {
                _bootstrapper.AutoCheckUpdates = !_bootstrapper.AutoCheckUpdates;
                _autoUpdateItem.Header = FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", _bootstrapper.AutoCheckUpdates);
            };
            menu.Items.Add(_autoUpdateItem);

            // Separator
            menu.Items.Add(new NativeMenuItemSeparator());

            // Exit
            _exitItem = new NativeMenuItem(loc["TrayExit"] ?? "Exit");
            _exitItem.Click += (s, e) => _desktop.Shutdown();
            menu.Items.Add(_exitItem);

            trayIcon.Menu = menu;

            loc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Item" || e.PropertyName == "Item[]")
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _showItem.Header = loc["TrayShow"] ?? "Show";
                        _exitItem.Header = loc["TrayExit"] ?? "Exit";
                        _autoStartItem.Header = FormatToggleLabel(loc["RunOnStartup"] ?? "Run On Startup", _bootstrapper.RunOnStartup);
                        _autoUpdateItem.Header = FormatToggleLabel(loc["AutoCheckUpdates"] ?? "Auto Check Updates", _bootstrapper.AutoCheckUpdates);
                    });
                }
            };

            var trayIcons = new TrayIcons();
            trayIcons.Add(trayIcon);

            _app.SetValue(TrayIcon.IconsProperty, trayIcons);
        }
        catch (Exception ex)
        {
            AppLog.Warning("App.TrayIconSetup", ex);
        }
    }

    private void ShowMainWindow()
    {
        var mainWindow = EnsureMainWindow();
        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private MainWindow EnsureMainWindow()
    {
        var mainWindow = _bootstrapper.CreateMainWindow();
        if (_desktop.MainWindow != mainWindow)
        {
            _desktop.MainWindow = mainWindow;
        }

        return mainWindow;
    }
}
