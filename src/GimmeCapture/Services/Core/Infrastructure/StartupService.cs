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
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                AppLog.Warning("StartupRegistration.Set", "Environment.ProcessPath was empty; cannot register run-on-startup.");
                return;
            }

            var expectedValue = $"\"{exePath}\" {RunArgumentForTrayStartup}";

            if (!runOnStartup)
            {
                // Clear BOTH backends: an install that predates the scheduled task still has a Run value, and one
                // that has since been upgraded still has the task. Leaving either behind keeps auto-starting the
                // app after the user switched the setting off.
                RemoveStartupTask(exePath);
                RemoveRunValue(exePath, "the setting is off");
                return;
            }

            // Elevated build → the Run key is a dead end (Windows silently skips elevated Run entries at logon),
            // so the scheduled task is the only registration that actually launches us. See
            // WindowsStartupTaskService for the full explanation.
            if (WindowsStartupTaskService.IsElevated() && TryRegisterStartupTask(exePath, expectedValue))
            {
                return;
            }

            RegisterRunValue(expectedValue);
        }
        catch (Exception ex)
        {
            AppLog.Warning("StartupRegistration.Set", ex);
        }
    }

    /// <summary>
    /// Registers the logon task and retires the Run value it replaces. Returns false only when the task could not
    /// be created, which is the caller's cue to fall back to the Run key — a registration Windows will ignore for
    /// an elevated build, but better than leaving the user with nothing while the failure is logged.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryRegisterStartupTask(string exePath, string expectedValue)
    {
        var existingTaskCommand = WindowsStartupTaskService.TryReadRegisteredCommandLine();
        if (existingTaskCommand != null && !ShouldClaimRegistration(existingTaskCommand, expectedValue))
        {
            // Same precedence as the Run key: another install that still exists on disk keeps the registration,
            // so a dev build launching does not repoint the logon task at bin\Debug.
            AppLog.Information(
                $"StartupTask: left another install's logon task alone -> {existingTaskCommand} (this copy: {expectedValue})");
            return true;
        }

        if (!WindowsStartupTaskService.TryRegister(exePath))
        {
            AppLog.Warning(
                "StartupRegistration.Set",
                "Could not create the logon task, so run-on-startup fell back to the HKCU Run key — which Windows "
                + "IGNORES for an elevated build. Auto-start will not work until the task can be created.");
            return false;
        }

        // The task now owns startup. A leftover Run value would only ever be skipped by Windows, and it is the
        // thing that made auto-start look configured while doing nothing, so take it out.
        RemoveRunValue(exePath, "the logon task replaces it");
        return true;
    }

    /// <summary>
    /// Removes the logon task, unless it belongs to a different install that is still on disk. A definition this
    /// process cannot read (a standard user cannot list System32\Tasks) leaves no way to tell, and there the
    /// user's explicit "off" wins over an owner we cannot identify.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveStartupTask(string exePath)
    {
        var existingTaskCommand = WindowsStartupTaskService.TryReadRegisteredCommandLine();
        if (existingTaskCommand != null && !OwnsRegistration(existingTaskCommand, exePath))
        {
            AppLog.Information(
                $"StartupTask: left another install's logon task alone -> {existingTaskCommand} (this copy: {exePath})");
            return;
        }

        WindowsStartupTaskService.TryUnregister();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RegisterRunValue(string expectedValue)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null)
        {
            AppLog.Warning("StartupRegistration.Set", "Could not open the HKCU Run key for writing.");
            return;
        }

        var existingValue = key.GetValue(AppName) as string;
        if (ShouldClaimRegistration(existingValue, expectedValue))
        {
            key.SetValue(AppName, expectedValue);
            AppLog.Information($"StartupRegistration: registered run-on-startup -> {expectedValue} (previous: {existingValue ?? "<none>"})");
        }
        else if (existingValue != expectedValue)
        {
            AppLog.Information(
                $"StartupRegistration: left another install's entry alone -> {existingValue} (this copy: {expectedValue})");
        }
    }

    /// <summary>
    /// Deletes our HKCU Run value, unless it belongs to a different install that is still on disk — removing that
    /// one would break the OTHER copy's auto-start, which is never this copy's business.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveRunValue(string exePath, string reason)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null)
        {
            AppLog.Warning("StartupRegistration.Set", "Could not open the HKCU Run key for writing.");
            return;
        }

        if (key.GetValue(AppName) is not string existingValue)
        {
            return;
        }

        if (!OwnsRegistration(existingValue, exePath))
        {
            AppLog.Information(
                $"StartupRegistration: left another install's entry alone -> {existingValue} (this copy: {exePath})");
            return;
        }

        key.DeleteValue(AppName);
        AppLog.Information($"StartupRegistration: removed the run-on-startup entry ({reason}).");
    }

    /// <summary>
    /// Whether a registration is this copy's to delete: it points at this executable, or at one that no longer
    /// exists (a leftover from an install that has been moved or removed). Shared by both backends so switching
    /// run-on-startup off cannot take out a different install's auto-start.
    /// </summary>
    internal static bool OwnsRegistration(string? existingValue, string exePath)
    {
        var existingExe = ExtractExecutablePath(existingValue);
        if (string.IsNullOrEmpty(existingExe))
        {
            return true;
        }

        return string.Equals(existingExe, exePath, StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(existingExe);
    }

    /// <summary>
    /// Whether a registration exists that Windows will actually act on at logon.
    ///
    /// An elevated build's Run value does NOT count: Windows silently skips elevated entries in HKCU\Run, so
    /// reporting one as "registered" is exactly the false green light that let auto-start be broken in every
    /// shipped build while the settings screen and the log both said it was fine.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static bool IsRegistered()
    {
        try
        {
            if (WindowsStartupTaskService.Exists())
            {
                return true;
            }

            if (WindowsStartupTaskService.IsElevated())
            {
                return false;
            }

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
            // StartupApproved governs the Run VALUE only. Once the logon task owns startup, a leftover blob from
            // when this install used the Run key says nothing about whether we launch — reporting it would send
            // the user to a Task Manager switch that no longer controls anything.
            if (WindowsStartupTaskService.Exists())
            {
                return false;
            }

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

    /// <summary>
    /// Whether this copy should take over the startup registration.
    ///
    /// Yes when there is nothing usable there: no entry at all, or one pointing at an executable that no longer
    /// exists (the reinstall/update case the re-assert exists for). NO when a valid entry points at a DIFFERENT
    /// install that is still on disk — otherwise whichever copy ran last wins, and a developer's bin\Debug build
    /// silently becomes the thing Windows launches at login, until the next clean build deletes that path and
    /// auto-start breaks with no visible cause.
    ///
    /// Pure so the precedence rules are unit-testable without touching the registry.
    /// </summary>
    internal static bool ShouldClaimRegistration(string? existingValue, string expectedValue)
    {
        if (string.IsNullOrWhiteSpace(existingValue)) return true;
        if (string.Equals(existingValue, expectedValue, StringComparison.OrdinalIgnoreCase)) return false;

        var existingExe = ExtractExecutablePath(existingValue);
        return string.IsNullOrEmpty(existingExe) || !System.IO.File.Exists(existingExe);
    }

    /// <summary>
    /// Pulls the executable out of a Run value. The value we write is <c>"path" --startup</c>, but a hand-edited
    /// or older entry may be unquoted, so both shapes are handled.
    /// </summary>
    internal static string? ExtractExecutablePath(string? runValue)
    {
        if (string.IsNullOrWhiteSpace(runValue)) return null;

        var value = runValue.Trim();
        if (value[0] == '"')
        {
            int closing = value.IndexOf('"', 1);
            return closing > 1 ? value[1..closing] : null;
        }

        int space = value.IndexOf(' ');
        return space < 0 ? value : value[..space];
    }
}
