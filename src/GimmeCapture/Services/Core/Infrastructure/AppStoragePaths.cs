using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GimmeCapture.Services.Core.Infrastructure;

internal static class AppStoragePaths
{
    private static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimmeCapture");

    public static string GetRootDirectory()
    {
        return RootDirectory;
    }

    public static string GetLegacyConfigPath()
    {
        return Path.Combine(RootDirectory, "config.json");
    }

    public static string GetInstallInstanceDirectory(string appDirectory)
    {
        return Path.Combine(RootDirectory, "instances", GetInstallInstanceKey(appDirectory));
    }

    public static string GetVersionedDataDirectory(string appDirectory, string appVersion)
    {
        return Path.Combine(
            GetInstallInstanceDirectory(appDirectory),
            "versions",
            NormalizeSegment(appVersion));
    }

    public static string GetVersionedConfigPath(string appDirectory, string appVersion)
    {
        return Path.Combine(GetVersionedDataDirectory(appDirectory, appVersion), "config.json");
    }

    public static string GetPendingUpdateStatePath(string appDirectory)
    {
        return Path.Combine(RootDirectory, "instances", GetInstallInstanceKey(appDirectory), "pending-update.json");
    }

    public static string GetFailedVerificationReportPath(string appDirectory)
    {
        return Path.Combine(RootDirectory, "instances", GetInstallInstanceKey(appDirectory), "pending-update.failed.txt");
    }

    public static string GetLegacyPendingUpdateStatePath()
    {
        return Path.Combine(RootDirectory, "pending-update.json");
    }

    public static string GetLegacyFailedVerificationReportPath()
    {
        return Path.Combine(RootDirectory, "pending-update.failed.txt");
    }

    public static string GetSharedAIResourcesDirectory(string appDirectory)
    {
        return Path.Combine(GetInstallInstanceDirectory(appDirectory), "shared", "AI");
    }

    public static bool TryMapVersionScopedAIResourcesDirectory(string aiResourcesDirectory, out string sharedDirectory)
    {
        sharedDirectory = string.Empty;

        if (string.IsNullOrWhiteSpace(aiResourcesDirectory))
        {
            return false;
        }

        try
        {
            string normalizedPath = Path.GetFullPath(aiResourcesDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var aiDir = new DirectoryInfo(normalizedPath);
            if (!string.Equals(aiDir.Name, "AI", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var versionDir = aiDir.Parent;
            var versionsDir = versionDir?.Parent;
            var instanceDir = versionsDir?.Parent;
            var instancesDir = instanceDir?.Parent;

            if (versionDir == null
                || versionsDir == null
                || instanceDir == null
                || instancesDir == null
                || !string.Equals(versionsDir.Name, "versions", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(instancesDir.Name, "instances", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            sharedDirectory = Path.Combine(instanceDir.FullName, "shared", "AI");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stable short key for one installed copy of the app (hash of its install directory).
    /// Also scopes the single-instance mutex so two different installs can still run side by side.</summary>
    internal static string GetInstallInstanceKey(string appDirectory)
    {
        string normalized = Path.GetFullPath(appDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    private static string NormalizeSegment(string value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().TrimStart('v');
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '_');
        }

        return normalized.Replace(' ', '_');
    }
}
