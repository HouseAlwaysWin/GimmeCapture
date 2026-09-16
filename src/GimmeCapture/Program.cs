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

    private static Action? _activationCallback;

    /// <summary>Wires the duplicate-launch activation callback and remembers it, so a guard taken back after a
    /// declined elevated restart keeps listening.</summary>
    internal static void StartActivationListener(Action onActivationRequested)
    {
        _activationCallback = onActivationRequested;
        SingleInstance?.StartActivationListener(onActivationRequested);
    }

    /// <summary>
    /// Hands the instance mutex over to an elevated copy of ourselves that is about to start. The new process
    /// acquires it while this one is still shutting down, and whoever loses decides it is a duplicate and exits —
    /// which would mean the user consents to UAC and the app simply disappears.
    /// </summary>
    internal static void ReleaseSingleInstanceForRestart() => SingleInstance?.Dispose();

    /// <summary>Takes the guard back when that restart did not happen after all (the user declined UAC).</summary>
    internal static void ReacquireSingleInstanceAfterFailedRestart()
    {
        SingleInstance = SingleInstanceGuard.TryAcquire();
        if (SingleInstance != null && _activationCallback != null)
        {
            SingleInstance.StartActivationListener(_activationCallback);
        }
    }

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
                // An auto-start launch that lost the race must stay quiet: a second registration (a Run value
                // plus a Startup-folder shortcut, or a leftover logon task) would otherwise pop the running
                // instance's main window at every sign-in. A duplicate the USER started still shows the app.
                if (StartupService.ShouldLaunchToTrayOnly(CommandLineArgs))
                {
                    AppLog.Information("Program.DuplicateLaunch.StartupLaunchIgnored");
                    return;
                }

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
