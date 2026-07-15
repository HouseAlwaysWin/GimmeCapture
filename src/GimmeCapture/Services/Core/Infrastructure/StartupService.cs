using Microsoft.Win32;
using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Infrastructure;

public class StartupService
{
    private const string AppName = "GimmeCapture";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Passed when Windows launches the app from Run; app should start only in the tray (no main window).</summary>
    public const string RunArgumentForTrayStartup = "--startup";

    public static bool ShouldLaunchToTrayOnly(IReadOnlyList<string>? args)
    {
        if (args == null || args.Count == 0) return false;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], RunArgumentForTrayStartup, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static void SetStartup(bool runOnStartup)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
            {
                AppLog.Warning("StartupRegistration.Set", "Could not open the HKCU Run key for writing.");
                return;
            }

            var existingValue = key.GetValue(AppName) as string;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                AppLog.Warning("StartupRegistration.Set", "Environment.ProcessPath was empty; cannot register run-on-startup.");
                return;
            }

            var expectedValue = runOnStartup
                ? $"\"{exePath}\" {RunArgumentForTrayStartup}"
                : null;

            if (runOnStartup)
            {
                // Write only when missing or stale (e.g. the exe path changed after a reinstall/update).
                if (existingValue != expectedValue)
                {
                    key.SetValue(AppName, expectedValue!);
                    AppLog.Information($"StartupRegistration: registered run-on-startup -> {expectedValue} (previous: {existingValue ?? "<none>"})");
                }
            }
            else
            {
                // Only delete if exists
                if (existingValue != null)
                {
                    key.DeleteValue(AppName);
                    AppLog.Information("StartupRegistration: removed the run-on-startup entry (setting is off).");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("StartupRegistration.Set", ex);
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;

            return key.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }
}
