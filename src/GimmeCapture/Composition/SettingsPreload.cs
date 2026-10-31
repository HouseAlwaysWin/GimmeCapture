using System;
using System.Threading;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Composition;

/// <summary>
/// Reads the saved settings on the thread pool from the start of Main, alongside Avalonia's own start-up, for
/// <see cref="AppBootstrapper"/> to take. The read used to begin only once the main view model existed and to finish
/// on the UI thread after the main window was up, and the global hotkeys register only once the settings are applied:
/// one pressed before then was lost.
/// </summary>
internal static class SettingsPreload
{
    private static AppSettingsService? _settingsService;

    /// <summary>Starts the read. Main calls it once, before Avalonia starts.</summary>
    internal static void Start()
    {
        try
        {
            var settingsService = new AppSettingsService();
            settingsService.StartReadingInBackground();
            _settingsService = settingsService;
        }
        catch (Exception ex)
        {
            // Never worth a failed start: the bootstrapper then loads the settings the way it did before this existed.
            AppLog.Warning("Startup.SettingsPreload", ex);
        }
    }

    /// <summary>The settings service <see cref="Start"/> began reading, handed out once; null when nothing started one.</summary>
    internal static AppSettingsService? Take() => Interlocked.Exchange(ref _settingsService, null);

    /// <summary>
    /// That service without handing it out, for what Avalonia's own start-up decides before the bootstrapper takes it
    /// (<see cref="StartupRenderer"/>). Null once taken, or when nothing started one.
    /// </summary>
    internal static AppSettingsService? Started => Volatile.Read(ref _settingsService);
}
