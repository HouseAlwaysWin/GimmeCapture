using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;

namespace GimmeCapture.Tests;

/// <summary>
/// Ending the app from OnFrameworkInitializationCompleted, before the lifetime's main loop starts, as App does when it
/// hands over to an elevated copy or an updated install. A plain Shutdown there made the main loop throw "Cannot perform
/// requested operation because the Dispatcher shut down", and the user saw a fatal start-up error after agreeing to UAC
/// (<see cref="App.ShutdownBeforeStart"/>). Opt-in (STARTUP_EXIT_PROBE=direct for the old way, posted for the new):
/// each starts a headless app lifetime, which a process can do only once.
/// </summary>
public sealed class AppShutdownBeforeStartTests
{
    [OptInFact("STARTUP_EXIT_PROBE=direct")]
    public void ShuttingDownDirectly_FailsTheStart()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Start(desktop => desktop.Shutdown()));
        Assert.Contains("Dispatcher shut down", error.Message);
    }

    [OptInFact("STARTUP_EXIT_PROBE=posted")]
    public void ShuttingDownBeforeStart_EndsTheMainLoopAsUsual() =>
        Assert.Equal(0, Start(App.ShutdownBeforeStart));

    private static int Start(Action<IClassicDesktopStyleApplicationLifetime> exit)
    {
        EarlyExitApp.Exit = exit;
        return AppBuilder.Configure<EarlyExitApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .StartWithClassicDesktopLifetime([]);
    }

    private sealed class EarlyExitApp : Application
    {
        internal static Action<IClassicDesktopStyleApplicationLifetime>? Exit;

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                Exit!(desktop);
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
