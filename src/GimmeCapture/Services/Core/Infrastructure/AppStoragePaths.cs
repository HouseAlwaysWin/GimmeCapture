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

    public static string GetVersionedDataDirectory(string appDirectory, string appVersion)
    {
        return Path.Combine(
            RootDirectory,
            "instances",
            GetInstallInstanceKey(appDirectory),
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

    private static string GetInstallInstanceKey(string appDirectory)
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
