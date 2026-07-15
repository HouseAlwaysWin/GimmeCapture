using System;
using System.IO;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Crash-safe text writes: write to a sibling temp file, then atomically swap it into place
/// (<see cref="File.Replace(string,string,string)"/> when the target exists, else <see cref="File.Move(string,string)"/>).
/// A crash or disk-full mid-write leaves the original file intact instead of a truncated one, so settings /
/// state stores never silently revert to defaults. The temp file lives in the same directory as the target so
/// the swap stays on one volume; on any failure it is best-effort deleted and the exception propagates to the
/// caller (which logs it).
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = PrepareTemp(path);
        try
        {
            File.WriteAllText(tmp, contents);
            Swap(tmp, path);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    public static async Task WriteAllTextAsync(string path, string contents)
    {
        var tmp = PrepareTemp(path);
        try
        {
            await File.WriteAllTextAsync(tmp, contents).ConfigureAwait(false);
            Swap(tmp, path);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static string PrepareTemp(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
    }

    private static void Swap(string tmp, string path)
    {
        if (File.Exists(path))
        {
            // Atomic on NTFS; preserves the destination's attributes.
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void TryDelete(string tmp)
    {
        try
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch
        {
            // Best-effort: the temp name is unique, so a rare leftover is harmless.
        }
    }
}
