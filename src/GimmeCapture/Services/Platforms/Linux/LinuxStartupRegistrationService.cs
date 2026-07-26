using System;
using System.IO;
using System.Reflection;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// Linux <see cref="IStartupRegistrationService"/> — XDG autostart, the equivalent of the Windows
/// HKCU\...\Run key. Writes/removes <c>~/.config/autostart/gimmecapture.desktop</c>. Mirrors the
/// Windows semantics: launches with <see cref="StartupService.RunArgumentForTrayStartup"/> so the
/// app starts in the background (docs/LINUX_PORT_FEASIBILITY.md).
/// </summary>
public sealed class LinuxStartupRegistrationService : IStartupRegistrationService
{
    private const string DesktopFileName = "gimmecapture.desktop";

    // Environment.SpecialFolder.ApplicationData maps to $XDG_CONFIG_HOME (~/.config) on Linux.
    private static string AutostartDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");

    private static string DesktopFilePath => Path.Combine(AutostartDir, DesktopFileName);

    public void SetStartup(bool runOnStartup)
    {
        try
        {
            if (runOnStartup)
            {
                Directory.CreateDirectory(AutostartDir);
                File.WriteAllText(DesktopFilePath, BuildDesktopEntry());
                AppLog.Information($"LinuxStartupRegistration.Enabled {DesktopFilePath}");
            }
            else if (File.Exists(DesktopFilePath))
            {
                File.Delete(DesktopFilePath);
                AppLog.Information("LinuxStartupRegistration.Disabled");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxStartupRegistration.SetStartup", ex);
        }
    }

    public bool IsRegistered()
    {
        try
        {
            return File.Exists(DesktopFilePath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// XDG autostart has no separate "the desktop environment disabled it" record the way Windows' StartupApproved
    /// does — the .desktop file existing is the whole state — so there is nothing extra to report here.
    /// </summary>
    public bool IsDisabledByOs() => false;

    private static string BuildDesktopEntry()
    {
        // Newlines only (\n) — .desktop files are LF-terminated key=value lines.
        return string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=GimmeCapture",
            "Comment=Screen capture tool",
            $"Exec={BuildExecCommand()}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "");
    }

    private static string BuildExecCommand()
    {
        // Framework-dependent runs report the dotnet host as ProcessPath, so append the entry dll;
        // a self-contained apphost reports itself and needs no dll argument.
        string host = Environment.ProcessPath ?? "dotnet";
        string entryDll = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
        bool isDotnetHost = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        string cmd = isDotnetHost && !string.IsNullOrEmpty(entryDll)
            ? $"\"{host}\" \"{entryDll}\""
            : $"\"{host}\"";

        return $"{cmd} {StartupService.RunArgumentForTrayStartup}";
    }
}
