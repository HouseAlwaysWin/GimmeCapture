using System;
using System.IO;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Best-effort startup sweep of leftover GimmeCapture temp workspaces. The per-operation
/// <c>GimmeCapture_Export_*/Compress_*/Estimate_*/Copy_*/Compare_*/Update_*/ClipExport_*/ClipAudio_*</c> dirs are
/// normally removed in a <c>finally</c>, but a hard crash or kill strands them; and the persistent
/// <c>GimmeCapture_Pins</c> files are cleaned when their owning pin disposes — a crash skips that too. This mops up
/// both. Age-gated so it can never delete a workspace a concurrently-running second instance is actively using.
/// </summary>
public static class TempWorkspaceCleaner
{
    // Only touch entries untouched for this long, so a live (even concurrent-instance) operation is never deleted.
    private static readonly TimeSpan OrphanDirMaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan PinFileMaxAge = TimeSpan.FromHours(24);

    /// <summary>Run the sweep. Never throws. Call off the UI thread once the app has settled.</summary>
    public static void SweepOrphans()
    {
        try
        {
            int dirs = SweepOrphanSessionDirectories();
            int pins = PinTempDirectory.SweepFilesOlderThan(PinFileMaxAge);
            if (dirs > 0 || pins > 0)
            {
                AppLog.Information($"TempWorkspaceCleaner: removed {dirs} orphan temp dir(s), {pins} stale pin file(s).");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("TempWorkspaceCleaner.Sweep", ex);
        }
    }

    private static int SweepOrphanSessionDirectories()
    {
        string temp = Path.GetTempPath();
        if (!Directory.Exists(temp))
        {
            return 0;
        }

        int deleted = 0;
        DateTime cutoffUtc = DateTime.UtcNow - OrphanDirMaxAge;
        foreach (string dir in Directory.EnumerateDirectories(temp, "GimmeCapture_*"))
        {
            // Keep the persistent pin folder itself; its stale contents are swept by age separately.
            if (string.Equals(Path.GetFileName(dir), PinTempDirectory.FolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoffUtc)
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
            }
            catch
            {
                // Locked / in use by a running instance — skip.
            }
        }

        return deleted;
    }
}
