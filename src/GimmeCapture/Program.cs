using Avalonia;
using System;
using Avalonia.ReactiveUI;

namespace GimmeCapture;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Ensure Working Directory is correct (Fix for Auto-Start)
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir))
                System.IO.Directory.SetCurrentDirectory(exeDir);

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Startup failure can happen before Avalonia UI is fully available,
            // so keep the fatal prompt on a platform-native dialog.
            var message = $"Application Startup Failed:\n{ex.Message}\n\nStack:\n{ex.StackTrace}";
            System.Windows.Forms.MessageBox.Show(message, "GimmeCapture Fatal Error");
            System.Diagnostics.Debug.WriteLine($"FATAL: {ex}");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
