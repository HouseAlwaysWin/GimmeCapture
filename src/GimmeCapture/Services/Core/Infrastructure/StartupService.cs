using Microsoft.Win32;
using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Infrastructure;

public class StartupService
{
    private const string AppName = "GimmeCapture";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Where Windows records that the user switched a startup entry off in Task Manager → "Startup apps".
    /// </summary>
    private const string StartupApprovedRunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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

    /// <summary>
    /// True when Windows itself has switched our startup entry OFF (Task Manager → "Startup apps" → Disable).
    /// Windows records that in <see cref="StartupApprovedRunKeyPath"/> and then IGNORES the Run value entirely —
    /// so the app can keep re-registering a perfectly valid Run entry and still never launch at boot, while
    /// <see cref="IsRegistered"/> happily reports true. Detecting this is the only way to tell the user the truth
    /// instead of showing an "on" switch that Windows silently overrides.
    /// </summary>
    /// <remarks>
    /// Value layout: a byte blob whose first byte is the state (0x02/0x06 = enabled, 0x03/0x07 = disabled — the
    /// low bit is the disabled flag), followed by a FILETIME of when it was disabled. A missing value means the
    /// user never touched it, which Windows treats as enabled. We only READ this: flipping it back would override
    /// an explicit user choice (and is exactly the behavior security tools flag), so we report and let them decide.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool IsDisabledByWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, false);
            return IsDisabledStateBlob(key?.GetValue(AppName) as byte[]);
        }
        catch
        {
            return false; // never let a registry hiccup block the settings UI
        }
    }

    /// <summary>
    /// Interprets a StartupApproved blob: the first byte carries the state, and its low bit is the "disabled"
    /// flag (0x02/0x06 = enabled, 0x03/0x07 = disabled). A missing/empty blob means the user never changed it,
    /// which Windows treats as enabled. Split out from the registry read so it can be unit-tested.
    /// </summary>
    internal static bool IsDisabledStateBlob(byte[]? state)
    {
        return state is { Length: > 0 } && (state[0] & 1) != 0;
    }
}
