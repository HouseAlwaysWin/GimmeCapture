using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GimmeCapture.ViewModels;
using GimmeCapture.Views;

namespace GimmeCapture;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
            
            // Setup Tray Icon
            try
            {
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
                    var showItem = new Avalonia.Controls.NativeMenuItem("Show");
                    showItem.Click += (s, e) => 
                    {
                         if (desktop.MainWindow != null)
                        {
                            desktop.MainWindow.Show();
                            desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                            desktop.MainWindow.Activate();
                        }
                    };
                    
                    var exitItem = new Avalonia.Controls.NativeMenuItem("Exit");
                    exitItem.Click += (s, e) => 
                    {
                        desktop.Shutdown();
                    };
                    
                    menu.Items.Add(showItem);
                    menu.Items.Add(exitItem);
                    trayIcon.Menu = menu;
                    
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