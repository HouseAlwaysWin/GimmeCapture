using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GimmeCapture.Composition;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture;

public partial class App : Application
{
    private AppBootstrapper? _bootstrapper;
    private TrayController? _trayController;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTrace.Mark("avalonia");

            // The one failure the log could never show: the UI thread blocked long enough that a hotkey press
            // only reaches the app seconds later, so "nothing happens" when the user asks for a capture. Started
            // first so it also reports a slow start-up.
            UiThreadStallMonitor.Start();

            var currentExePath = Environment.ProcessPath ?? string.Empty;
            if (UpdateService.TryRedirectToPendingUpdatedInstance(AppVersionInfo.CurrentVersion, currentExePath))
            {
                desktop.Shutdown();
                return;
            }

            var verificationResult = UpdateService.VerifyPendingUpdateOnStartup(AppVersionInfo.CurrentVersion, currentExePath);
            if (verificationResult.HasPendingUpdate && !verificationResult.IsSuccess)
            {
                PlatformErrorDialog.ShowError(
                    $"Update verification failed.{Environment.NewLine}{Environment.NewLine}{verificationResult.FailureMessage}",
                    "Update Verification Failed");
                desktop.Shutdown();
                return;
            }

            StartupTrace.Mark("pendingUpdate");

            _bootstrapper = new AppBootstrapper();
            StartupTrace.Mark("bootstrapper");
            if (_bootstrapper.TryHandOverToElevatedCopy())
            {
                desktop.Shutdown();
                return;
            }

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
                _bootstrapper.DisposeAsync().AsTask().GetAwaiter().GetResult();
            // After that dispose, which the stall watchdog still covers: past it the UI thread stops pumping for good,
            // and the watchdog would report the rest of the exit as a stall.
            desktop.Exit += (_, _) => UiThreadStallMonitor.Stop();
            // The main view model applies the saved settings as it is built, hotkeys included: Main has read them
            // (SettingsPreload).
            _bootstrapper.CreateMainViewModel();
            StartupTrace.Mark("viewModel");
            TraceSettingsAppliedAsync(_bootstrapper.InitialSettingsLoadTask).Forget("Startup.Trace");

            var launchToTrayOnly = StartupService.ShouldLaunchToTrayOnly(Program.CommandLineArgs);
            if (!launchToTrayOnly)
            {
                var mainWindow = _bootstrapper.CreateMainWindow();
                StartupTrace.Mark("mainWindow");
                mainWindow.Opened += OnMainWindowFirstOpened;
                desktop.MainWindow = mainWindow;
            }
            else
            {
                StartupTrace.LaunchedToTray();
                // With no main window, the tray host stands in as the lifetime's, which shows it: the visible top level
                // the clipboard and dialogs need (see AppBootstrapper.CreateTrayHostWindow).
                desktop.MainWindow = _bootstrapper.CreateTrayHostWindow();
            }

            // The global hotkeys work from here. This used to wait for that hidden window to open, which a window launch
            // has no other use for at start-up, and for the settings, applied only once the main window was up.
            _bootstrapper.InitializeHotkeys();
            StartupTrace.MarkHotkeysInitialized();

            // Tray icon + native menu lifecycle lives in TrayController.
            _trayController = new TrayController(this, _bootstrapper, desktop);
            _trayController.Install();

            // The first capture of a run used to load the overlay's code on the spot, on the UI thread.
            _bootstrapper.ScheduleCaptureOverlayWarmUp();

            // Once the app settles into idle, do a FULL trim (GC to collect startup/JIT garbage + working-set
            // reclaim). Decoupled from the window-shown activity so it isn't immediately cancelled; the scheduler
            // still skips it if the user is interacting. Note: while the main window is open and rendering,
            // Windows keeps its working set resident, so this reaches a lower baseline (not the tray <10MB).
            AppBootstrapper bootstrapper = _bootstrapper;
            var settingsLoad = _bootstrapper.InitialSettingsLoadTask;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                // Best-effort mop-up of temp workspaces a previous crash/kill left behind (age-gated; safe with a
                // concurrent second instance). Runs off the UI thread at settle so it never delays startup.
                TempWorkspaceCleaner.SweepOrphans();
                // Up to 478 MB per earlier version, unpacked by single-file builds and never removed.
                BundleExtractionCleaner.SweepStaleCopies();
                // Empty instance folders, AI files of features that are gone, and the copy of every model older
                // versions kept beside the AI folder in use: GBs per install. The settings name that folder.
                await settingsLoad.ConfigureAwait(System.Threading.Tasks.ConfigureAwaitOptions.SuppressThrowing);
                if (settingsLoad.IsCompletedSuccessfully)
                {
                    InstanceStorageCleaner.Sweep(bootstrapper.GetAIResourcesDirectoryInUse());
                    // Recordings nobody saved: copied to the clipboard, left by a crash, or in the Temp folder of a
                    // version since updated. A pinned one goes when its pin closes; the settings name the folders.
                    await bootstrapper.SweepLeftoverRecordingsAsync().ConfigureAwait(false);
                }
                // No explicit delay any more: the trim waits out the idle window the service enforces, so a user
                // who captures something in the first minutes never pays for a trim they are about to undo.
                await ProcessMemoryTrimService.RequestIdleTrimAsync("startup-settled").ConfigureAwait(false);
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnMainWindowFirstOpened(object? sender, EventArgs e)
    {
        if (sender is not Avalonia.Controls.Window window)
        {
            return;
        }

        window.Opened -= OnMainWindowFirstOpened;
        StartupTrace.Mark("show");
        // The first callback comes as the first frame starts; the second once that frame has been drawn.
        window.RequestAnimationFrame(_ => window.RequestAnimationFrame(_ => StartupTrace.MarkFirstFrame()));
    }

    private static async System.Threading.Tasks.Task TraceSettingsAppliedAsync(System.Threading.Tasks.Task settingsLoad)
    {
        try
        {
            await settingsLoad;
        }
        finally
        {
            StartupTrace.MarkSettingsApplied();
        }
    }
}
