using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Auto-start backed by a logon-triggered Windows scheduled task instead of the HKCU Run key.
///
/// Why this exists: Release builds ship with <c>app.admin.manifest</c> (<c>requireAdministrator</c>), and Windows
/// SILENTLY SKIPS elevated programs listed in <c>HKCU\...\Run</c> at logon — UAC cannot prompt for consent that
/// early, so the entry is simply never executed. No process, no error dialog, nothing in the event log, and not a
/// line in our own log because the app never reaches <c>Main</c>. The registry entry meanwhile looks perfect, so
/// every self-check we had reported "registered" while auto-start had in fact NEVER worked in a shipped build.
/// It only ever worked in dev, where <c>app.manifest</c> (<c>asInvoker</c>) is used.
///
/// A logon trigger with <c>RunLevel=HighestAvailable</c> is the supported way to start an elevated app at sign-in
/// without a UAC prompt, so that is what an elevated build registers. Creating such a task itself needs admin
/// rights, which an elevated build has; unelevated builds keep using the Run key, where it works fine.
/// </summary>
internal static class WindowsStartupTaskService
{
    /// <summary>Task name at the Task Scheduler root. Shared by every install, exactly like the Run value name,
    /// so the same "don't steal another install's registration" precedence applies.</summary>
    internal const string TaskName = "GimmeCapture";

    /// <summary>schtasks is a local, non-networked call; anything slower than this is hung, not slow.</summary>
    private const int SchtasksTimeoutMs = 15_000;

    /// <summary>
    /// Wait before launching at sign-in. A logon-triggered task can fire before Explorer has created the
    /// notification area, and a tray-only launch that loses that race comes up with no visible tray icon.
    /// The Run key never had this problem because the shell processes it after the taskbar exists.
    /// </summary>
    private const string LogonDelay = "PT10S";

    /// <summary>Path of the task definition Task Scheduler keeps on disk. Reading it avoids spawning schtasks
    /// just to answer "is it registered / where does it point", and it is the only locale-proof way to get the
    /// command back out (<c>schtasks /Query /FO LIST</c> prints localized field names).</summary>
    private static string TaskDefinitionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32",
        "Tasks",
        TaskName);

    /// <summary>
    /// Whether this process can register an elevated task at all. Also the signal that the Run key is useless to
    /// us: a process running elevated came from a manifest Windows refuses to auto-run from HKCU\Run.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsElevated() => Environment.IsPrivilegedProcess;

    /// <summary>True when a task by our name exists, whether or not it points at this copy of the app.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool Exists()
    {
        try
        {
            if (File.Exists(TaskDefinitionPath))
            {
                return true;
            }

            // An elevated process can always read System32\Tasks, so "not there" is conclusive and there is no
            // reason to pay for a schtasks launch on every settings load.
            if (IsElevated())
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            // Unreadable Tasks folder (standard user) — fall through to schtasks, which answers via exit code.
            AppLog.Warning("StartupTask.Exists", ex);
        }

        // Exit code only: 0 = found, non-zero = not found. Locale-independent, unlike the printed output.
        return TryRunSchtasks($"/Query /TN \"{TaskName}\"", out int exitCode, logFailure: false) && exitCode == 0;
    }

    /// <summary>
    /// The command line the registered task runs, in the same <c>"exe" args</c> shape as a Run value so it can be
    /// fed straight to <see cref="StartupService.ShouldClaimRegistration"/>. Null when there is no task, or when
    /// the definition cannot be read (a standard user cannot list <c>System32\Tasks</c>).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static string? TryReadRegisteredCommandLine()
    {
        try
        {
            if (!File.Exists(TaskDefinitionPath))
            {
                return null;
            }

            return ExtractCommandLine(File.ReadAllText(TaskDefinitionPath));
        }
        catch (Exception ex)
        {
            AppLog.Warning("StartupTask.Read", ex);
            return null;
        }
    }

    /// <summary>
    /// Registers (or overwrites) the logon task so it launches <paramref name="exePath"/> in the tray at sign-in.
    /// Returns false when the task could not be created — the caller then falls back to the Run key rather than
    /// leaving the user with no registration at all.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool TryRegister(string exePath)
    {
        string? xmlPath = null;
        try
        {
            string xml = BuildTaskXml(exePath, Environment.UserDomainName + "\\" + Environment.UserName);

            // schtasks /Create /XML wants a Unicode file; write UTF-16LE with a BOM to match the declaration.
            xmlPath = Path.Combine(Path.GetTempPath(), $"GimmeCapture.startup.{Guid.NewGuid():N}.xml");
            File.WriteAllText(xmlPath, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            if (!TryRunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out int exitCode)
                || exitCode != 0)
            {
                return false;
            }

            AppLog.Information($"StartupTask: registered the logon task -> \"{exePath}\" {StartupService.RunArgumentForTrayStartup}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("StartupTask.Register", ex);
            return false;
        }
        finally
        {
            if (xmlPath != null)
            {
                try { File.Delete(xmlPath); } catch { /* best-effort temp cleanup */ }
            }
        }
    }

    /// <summary>Removes the logon task. A task that was not there counts as success.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool TryUnregister()
    {
        if (!Exists())
        {
            return true;
        }

        if (!TryRunSchtasks($"/Delete /TN \"{TaskName}\" /F", out int exitCode) || exitCode != 0)
        {
            return false;
        }

        AppLog.Information("StartupTask: removed the logon task.");
        return true;
    }

    /// <summary>
    /// Pulls <c>"command" arguments</c> out of a Task Scheduler definition. Split out from the file read so the
    /// parsing is unit-testable, and deliberately string-based: the definition is a fixed, machine-written shape
    /// and this must not fail the settings screen over an unexpected namespace or attribute.
    /// </summary>
    internal static string? ExtractCommandLine(string? taskXml)
    {
        string? command = ExtractElement(taskXml, "Command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim().Trim('"');
        string? arguments = ExtractElement(taskXml, "Arguments")?.Trim();

        return string.IsNullOrEmpty(arguments) ? $"\"{command}\"" : $"\"{command}\" {arguments}";
    }

    private static string? ExtractElement(string? xml, string name)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        string open = "<" + name + ">";
        int start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        int end = xml.IndexOf("</" + name + ">", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? null : Decode(xml[start..end]);
    }

    private static string Decode(string value)
    {
        return value
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);
    }

    /// <summary>
    /// The task definition. <c>HighestAvailable</c> plus <c>InteractiveToken</c> is what makes an elevated app
    /// start at sign-in with no UAC prompt; the battery/idle/time-limit settings are all turned off because this
    /// is a tray app that must simply stay running, not a maintenance job the scheduler may defer or kill.
    /// </summary>
    internal static string BuildTaskXml(string exePath, string userId)
    {
        string workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
        string command = SecurityElement.Escape(exePath) ?? string.Empty;
        string user = SecurityElement.Escape(userId) ?? string.Empty;
        string workDir = SecurityElement.Escape(workingDirectory) ?? string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts GimmeCapture in the tray when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>{LogonDelay}</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{StartupService.RunArgumentForTrayStartup}</Arguments>
                  <WorkingDirectory>{workDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool TryRunSchtasks(string arguments, out int exitCode, bool logFailure = true)
    {
        exitCode = -1;
        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return false;
            }

            // Drain both pipes asynchronously: a full pipe would block schtasks forever, and WaitForExit with a
            // timeout is the only thing standing between a wedged child and a frozen settings toggle.
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(SchtasksTimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                AppLog.Warning("StartupTask.Schtasks", $"schtasks {arguments} timed out after {SchtasksTimeoutMs}ms.");
                return false;
            }

            exitCode = process.ExitCode;
            if (exitCode != 0 && logFailure)
            {
                AppLog.Warning(
                    "StartupTask.Schtasks",
                    $"schtasks {arguments} exited {exitCode}. {stderr.ToString().Trim()}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
            {
                AppLog.Warning("StartupTask.Schtasks", ex);
            }

            return false;
        }
    }
}
