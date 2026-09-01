using Avalonia;
using System;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI.Avalonia;

namespace GimmeCapture;

class Program
{
    /// <summary>Command-line arguments passed to <see cref="Main"/> (e.g. <c>--startup</c> from Windows Run).</summary>
    public static string[] CommandLineArgs { get; private set; } = [];

    /// <summary>The single-instance mutex owner for this process; TrayController wires its activation
    /// listener so a duplicate launch pops the running instance's main window. Null in a duplicate.</summary>
    internal static SingleInstanceGuard? SingleInstance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CommandLineArgs = args ?? [];
        AppLog.Initialize();
        try
        {
            // One running app per installed copy: a duplicate launch (double-clicked twice, autostart
            // racing a manual start) hands off to the running instance instead of opening a second one.
            SingleInstance = SingleInstanceGuard.TryAcquire();
            if (SingleInstance == null)
            {
                AppLog.Information("Program.DuplicateLaunch.HandedOffToRunningInstance");
                SingleInstanceGuard.SignalRunningInstance();
                return;
            }

            // Ensure Working Directory is correct (Fix for Auto-Start)
            var exeDir = RuntimePathProvider.GetExecutableDirectory();
            if (!string.IsNullOrEmpty(exeDir))
                System.IO.Directory.SetCurrentDirectory(exeDir);

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args ?? []);
        }
        catch (Exception ex)
        {
            AppLog.Error("Program.Startup", ex);
            // Startup failure can happen before Avalonia UI is fully available,
            // so keep the fatal prompt on a platform-native dialog.
            var message = $"Application Startup Failed:\n{ex.Message}\n\nStack:\n{ex.StackTrace}";
            PlatformErrorDialog.ShowError(message, "GimmeCapture Fatal Error");
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                Services.Platforms.Linux.LinuxWindowShape.Shutdown();
            }
            SingleInstance?.Dispose();
            AppLog.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI(_ => { });
}
