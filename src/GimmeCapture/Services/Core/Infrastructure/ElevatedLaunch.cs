using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using GimmeCapture.Services.Interop;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>What this process can do about administrator rights.</summary>
public enum AdministratorRights
{
    /// <summary>
    /// Not Windows, or an account UAC cannot elevate as itself: a standard user would have to sign in as another account,
    /// whose settings and history are not this user's.
    /// </summary>
    Unavailable,

    /// <summary>An administrator running with UAC's filtered token: one UAC prompt elevates this same user.</summary>
    CanElevate,

    /// <summary>Running elevated already.</summary>
    Elevated,
}

/// <summary>
/// Running as administrator (<see cref="Models.AppSettings.RunAsAdministrator"/>). While a window of a higher integrity
/// level has the foreground, Task Manager or anything else run as administrator, Windows withholds every key from a
/// process that is not elevated (UIPI): WM_HOTKEY, raw input, the low-level hook and GetAsyncKeyState alike, so only an
/// elevated GimmeCapture hears its hotkeys there. The exe stays asInvoker, because Windows silently skips a Run-key entry
/// whose exe demands elevation at sign-in (<see cref="StartupService"/>); with the setting on, the app instead asks UAC
/// for an elevated copy of itself as it starts.
/// </summary>
internal static class ElevatedLaunch
{
    /// <summary>Marks the copy a relaunch started: one that is still not elevated must not ask again.</summary>
    internal const string RelaunchedArgument = "--elevated-relaunch";

    private const int ErrorCancelled = 1223;

    // TOKEN_INFORMATION_CLASS.TokenElevationType, and the TOKEN_ELEVATION_TYPE values it answers with.
    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    private static AdministratorRights? _current;

    /// <summary>This process's rights, read once: one token query, where WindowsIdentity cost about 8 ms cold.</summary>
    internal static AdministratorRights Current => _current ??= ReadCurrent();

    private static AdministratorRights ReadCurrent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return AdministratorRights.Unavailable;
        }

        // Limited is an administrator's filtered token, which one UAC prompt elevates as the same user. Default is a
        // standard user (UAC would sign in somebody else), or UAC switched off, where everything already runs elevated.
        return ReadElevationType() switch
        {
            TokenElevationTypeFull => AdministratorRights.Elevated,
            TokenElevationTypeLimited => AdministratorRights.CanElevate,
            _ => AdministratorRights.Unavailable,
        };
    }

    internal static int ReadElevationType()
    {
        if (!Advapi32.OpenProcessToken(Kernel32.GetCurrentProcess(), Advapi32.TOKEN_QUERY, out IntPtr token))
        {
            return 0;
        }

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            return Advapi32.GetTokenInformation(token, TokenElevationTypeClass, buffer, sizeof(int), out _)
                ? Marshal.ReadInt32(buffer)
                : 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Kernel32.CloseHandle(token);
        }
    }

    /// <summary>Whether start-up hands over to an elevated copy: the setting is on and one UAC prompt can elevate us.</summary>
    internal static bool ShouldRelaunchAtStart(bool runAsAdministrator, AdministratorRights rights, IReadOnlyList<string> args) =>
        runAsAdministrator && rights == AdministratorRights.CanElevate && !IsRelaunchedCopy(args);

    internal static bool IsRelaunchedCopy(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, RelaunchedArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The elevated copy's arguments: the relaunch marker, after <c>--startup</c> when this launch had it, so a sign-in
    /// launch stays in the tray. No other argument means anything to the app.
    /// </summary>
    internal static string BuildArguments(IReadOnlyList<string> args) =>
        StartupService.ShouldLaunchToTrayOnly(args)
            ? $"{StartupService.RunArgumentForTrayStartup} {RelaunchedArgument}"
            : RelaunchedArgument;

    /// <summary>
    /// Asks UAC for an elevated copy of this exe, and blocks until the user answers. True once the copy has started;
    /// false when the user declined or it could not start.
    /// </summary>
    internal static bool TryStartElevatedCopy(IReadOnlyList<string> args)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        // The prompt holds the UI thread until the user answers, which is no stall to report.
        using var paused = UiThreadStallMonitor.Pause();
        try
        {
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = BuildArguments(args),
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            AppLog.Information("Elevation.Declined: the UAC prompt was declined; running without administrator rights.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warning("Elevation.StartElevatedCopy", ex);
            return false;
        }
    }
}
