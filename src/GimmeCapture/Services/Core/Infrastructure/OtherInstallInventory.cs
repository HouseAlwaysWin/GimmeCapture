using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>One other copy of the app on this PC, as its instance folder tells it.</summary>
/// <param name="ExecutablePath">Null for a folder written before installs recorded themselves.</param>
/// <param name="IsGone">Its executable is no longer where it ran from.</param>
/// <param name="Version">The version that last ran it, when known.</param>
/// <param name="IsRunning">It is running now, so its folder cannot be removed.</param>
internal sealed record OtherInstall(
    string Key,
    string Directory,
    string? ExecutablePath,
    bool IsGone,
    string? Version,
    DateTime LastUsedUtc,
    long SizeBytes,
    bool IsRunning);

/// <summary>
/// The other copies of the app whose data sits under instances\: every folder the app runs from keeps its own settings
/// and AI modules there, so a copy moved, reinstalled elsewhere or deleted leaves gigabytes behind. Listing them and
/// removing one the user chose is all this does; the start-up sweep never deletes another copy's data.
/// </summary>
internal static class OtherInstallInventory
{
    /// <summary>Every other install's instance folder that holds files, largest first. Folders with none are left to
    /// the start-up sweep.</summary>
    public static IReadOnlyList<OtherInstall> Scan(string instancesDirectory, string currentKey, Func<string, bool> isRunning)
    {
        if (!Directory.Exists(instancesDirectory))
        {
            return Array.Empty<OtherInstall>();
        }

        var installs = new List<OtherInstall>();
        foreach (var instance in new DirectoryInfo(instancesDirectory).EnumerateDirectories())
        {
            if (!AppStoragePaths.IsInstallInstanceKey(instance.Name)
                || string.Equals(instance.Name, currentKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var files = instance.EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                if (files.Count == 0)
                {
                    continue;
                }

                var record = InstallInstanceRecord.Read(instance.FullName);
                installs.Add(new OtherInstall(
                    instance.Name,
                    instance.FullName,
                    record?.ExecutablePath,
                    record != null && !File.Exists(record.ExecutablePath),
                    record?.Version ?? NewestVersionFolder(instance),
                    record?.LastRunUtc ?? files.Max(file => file.LastWriteTimeUtc),
                    files.Sum(file => file.Length),
                    isRunning(instance.Name)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Being written or removed right now: the next look lists it.
            }
        }

        return installs.OrderByDescending(install => install.SizeBytes).ToList();
    }

    /// <summary>Deletes <paramref name="install"/>'s instance folder. Null once it is gone, otherwise why it is not.</summary>
    public static string? Remove(OtherInstall install)
    {
        try
        {
            Directory.Delete(install.Directory, recursive: true);
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("OtherInstalls.Remove", ex);
            return ex.Message;
        }
    }

    // A folder from before installs recorded themselves still has one config folder per version it ran.
    private static string? NewestVersionFolder(DirectoryInfo instance)
    {
        var versions = new DirectoryInfo(Path.Combine(instance.FullName, "versions"));
        if (!versions.Exists)
        {
            return null;
        }

        return versions.EnumerateDirectories()
            .Select(directory => (directory.Name, Parsed: System.Version.TryParse(directory.Name, out var parsed) ? parsed : null))
            .Where(version => version.Parsed != null)
            .OrderByDescending(version => version.Parsed)
            .Select(version => version.Name)
            .FirstOrDefault();
    }
}
