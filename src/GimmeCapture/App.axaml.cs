using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GimmeCapture.Composition;
using GimmeCapture.Services.Core.Infrastructure;
using System.Diagnostics;

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
            desktop.Exit += (_, _) =>
                _bootstrapper.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

            // Tray icon + native menu lifecycle lives in TrayController.
            _trayController = new TrayController(this, _bootstrapper, desktop);
            _trayController.Install();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
