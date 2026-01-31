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
        
        // Generate placeholder icon if missing
        var iconPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "gimme_icon.ico");
        if (!System.IO.File.Exists(iconPath))
        {
            try 
            {
               // Create a simple 64x64 bitmap
               using var bitmap = new SkiaSharp.SKBitmap(64, 64);
               using var canvas = new SkiaSharp.SKCanvas(bitmap);
               
               // Draw background
               canvas.Clear(SkiaSharp.SKColors.Crimson);
               
               // Draw 'G'
               using var paint = new SkiaSharp.SKPaint
               {
                   Color = SkiaSharp.SKColors.Gold,
                   IsAntialias = true,
                   TextSize = 40,
                   TextAlign = SkiaSharp.SKTextAlign.Center
               };
               canvas.DrawText("G", 32, 48, paint);
               
               // Save as PNG (Avalonia loads PNG as icon just fine usually)
               // Note: Naming it .ico but it is a PNG stream. Windows generic icon loader often handles this, 
               // or Avalonia's WindowIcon helper does. If not, we might need real ICO format.
               // Let's safe as .ico extension but png content.
               using var fs = System.IO.File.OpenWrite(iconPath);
               bitmap.Encode(fs, SkiaSharp.SKEncodedImageFormat.Png, 100);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to generate icon: {ex.Message}");
            }
        }
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
                var iconPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "gimme_icon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    var trayIcon = new Avalonia.Controls.TrayIcon
                    {
                        Icon = new Avalonia.Controls.WindowIcon(iconPath),
                        ToolTipText = "GimmeCapture"
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
                    
                    desktop.MainWindow.SetValue(Avalonia.Controls.TrayIcon.IconsProperty, trayIcons);
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting up tray icon: {ex.Message}");
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}