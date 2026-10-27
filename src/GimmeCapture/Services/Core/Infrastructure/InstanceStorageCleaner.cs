using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Reclaims the per-install data that nothing uses any more. Every folder the app runs from gets an instance folder of
/// its own (instances\&lt;hash of that folder&gt;), and nothing ever removed one, or anything in one:
/// <list type="bullet">
/// <item>Instance folders holding no files. Reading the update state used to create one for every build and test
/// folder that ever ran: 203 on one machine.</item>
/// <item>Files of AI features that are gone: the ONNX Runtime AI Core downloaded (671 MB with its CUDA provider), the
/// MarianMT translator, the Vosk speech models and MobileSAM, plus downloads a killed process left half done.</item>
/// <item>The copy of every model that older versions made in the instance's shared AI folder while the settings kept
/// using another one, the executable's own AI folder as a rule: 3.4 GB on one machine.</item>
/// </list>
/// It also records which folder this install runs from (<see cref="InstallInstanceRecord"/>). Another install's files
/// are never deleted here: whether those can go is the user's call, in the Modules tab.
/// </summary>
public static class InstanceStorageCleaner
{
    // A folder another install is creating right now is empty for a moment. This is far longer.
    private static readonly TimeSpan EmptyInstanceMinimumAge = TimeSpan.FromHours(1);

    // A download in progress keeps writing its file. One untouched this long belongs to a download that was killed.
    private static readonly TimeSpan PartialDownloadMinimumAge = TimeSpan.FromHours(12);

    // AppStoragePaths.GetInstallInstanceKey: 16 lowercase hex digits. Anything else under instances\ is left alone.
    private static readonly Regex InstanceKeyPattern = new("^[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    // What AI Core unpacked into runtime\, and the zip it downloaded (temp_ai.zip before the downloader named files).
    private static readonly HashSet<string> RuntimeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "onnxruntime.dll", "onnxruntime_providers_cuda.dll", "onnxruntime_providers_shared.dll",
        "onnxruntime_providers_tensorrt.dll", "onnxruntime-win-x64-gpu-1.20.1.zip", "temp_ai.zip",
    };

    // The MarianMT (m2m100) translator's nmt\ folder.
    private static readonly HashSet<string> MarianMtFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "encoder_model.onnx", "decoder_model.onnx", "tokenizer.json", "sentencepiece.bpe.model",
        "config.json", "generation_config.json",
    };

    // MobileSAM, which SAM2 replaced.
    private static readonly string[] MobileSamFiles = { "mobile_sam_image_encoder.onnx", "sam_mask_decoder_multi.onnx" };

    // Where the downloaders write, and the kinds of file they write there.
    private static readonly (string Directory, string[] Extensions)[] DownloadTargets =
    {
        ("models", new[] { ".onnx" }),
        ("ocr", new[] { ".onnx", ".txt" }),
        (Path.Combine("llm", "models"), new[] { ".gguf" }),
    };

    internal sealed record Layout(
        string InstancesDirectory,
        string CurrentKey,
        string SharedAIDirectory,
        string LegacyAIDirectory,
        string AIDirectoryInUse);

    internal sealed record SweepResult(int EmptyInstancesRemoved, int FilesRemoved, long BytesFreed);

    /// <summary>Run the sweep. Never throws. Call off the UI thread, once the settings have loaded:
    /// <paramref name="aiDirectoryInUse"/> is the AI resources folder they use.</summary>
    public static void Sweep(string aiDirectoryInUse)
    {
        try
        {
            string appDirectory = RuntimePathProvider.GetExecutableDirectory();
            string instanceDirectory = AppStoragePaths.GetInstallInstanceDirectory(appDirectory);
            InstallInstanceRecord.Write(
                instanceDirectory,
                new InstallInstanceRecord(appDirectory, AppVersionInfo.CurrentVersion, DateTime.UtcNow));

            var result = Sweep(
                new Layout(
                    Path.GetDirectoryName(instanceDirectory)!,
                    Path.GetFileName(instanceDirectory),
                    AppStoragePaths.GetSharedAIResourcesDirectory(appDirectory),
                    Path.Combine(appDirectory, "AI"),
                    aiDirectoryInUse),
                DateTime.UtcNow);
            if (result.EmptyInstancesRemoved > 0 || result.FilesRemoved > 0)
            {
                AppLog.Information(
                    "InstanceStorage.Sweep",
                    $"removed {result.EmptyInstancesRemoved} empty instance folders and {result.FilesRemoved} unused files " +
                    $"({result.BytesFreed / (1024 * 1024)} MB)");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("InstanceStorage.Sweep", ex);
        }
    }

    internal static SweepResult Sweep(Layout layout, DateTime nowUtc)
    {
        int emptyInstances = RemoveEmptyInstances(layout.InstancesDirectory, layout.CurrentKey, nowUtc);

        var removed = new Tally();
        foreach (string aiDirectory in DistinctExisting(layout.SharedAIDirectory, layout.LegacyAIDirectory, layout.AIDirectoryInUse))
        {
            RemoveObsoleteAIFiles(aiDirectory, nowUtc, removed);
        }

        RemoveCopies(layout.AIDirectoryInUse, layout.SharedAIDirectory, removed);
        return new SweepResult(emptyInstances, removed.Files, removed.Bytes);
    }

    private static int RemoveEmptyInstances(string instancesDirectory, string currentKey, DateTime nowUtc)
    {
        if (!Directory.Exists(instancesDirectory))
        {
            return 0;
        }

        int removed = 0;
        foreach (string instance in Directory.EnumerateDirectories(instancesDirectory).ToList())
        {
            string key = Path.GetFileName(instance);
            if (!InstanceKeyPattern.IsMatch(key) || string.Equals(key, currentKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (TryRemoveEmptyTree(instance, nowUtc - EmptyInstanceMinimumAge))
                {
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something was written into it meanwhile, or a scanner has it open: the next launch looks again.
            }
        }

        return removed;
    }

    // A tree with no files, none of whose folders changed since the cutoff. Deleted one empty folder at a time, deepest
    // first, so a file another process writes meanwhile stops the deletion instead of going with it.
    private static bool TryRemoveEmptyTree(string root, DateTime cutoffUtc)
    {
        if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
        {
            return false;
        }

        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root).ToList();
        if (directories.Any(directory => Directory.GetLastWriteTimeUtc(directory) > cutoffUtc))
        {
            return false;
        }

        foreach (string directory in directories.OrderByDescending(directory => directory.Length))
        {
            Directory.Delete(directory, recursive: false);
        }

        return true;
    }

    private static void RemoveObsoleteAIFiles(string aiDirectory, DateTime nowUtc, Tally removed)
    {
        // A folder goes only when everything in it is what the app put there: the AI folder in use may be one the user
        // picked, where a folder of the same name could hold anything.
        RemoveFolderIfOnly(
            Path.Combine(aiDirectory, "runtime"),
            entry => entry is FileInfo && RuntimeFiles.Contains(WithoutDownloadSuffix(entry.Name)),
            removed);
        RemoveFolderIfOnly(
            Path.Combine(aiDirectory, "nmt"),
            entry => entry is FileInfo && MarianMtFiles.Contains(WithoutDownloadSuffix(entry.Name)),
            removed);
        RemoveFolderIfOnly(
            Path.Combine(aiDirectory, "stt"),
            entry => entry.Name.StartsWith("vosk-model", StringComparison.OrdinalIgnoreCase),
            removed);

        foreach (string name in MobileSamFiles)
        {
            RemoveFile(new FileInfo(Path.Combine(aiDirectory, "models", name)), removed);
        }

        foreach (var (relativeDirectory, extensions) in DownloadTargets)
        {
            var directory = new DirectoryInfo(Path.Combine(aiDirectory, relativeDirectory));
            if (!directory.Exists)
            {
                continue;
            }

            foreach (FileInfo file in directory.EnumerateFiles())
            {
                string name = WithoutDownloadSuffix(file.Name);
                if (name.Length != file.Name.Length
                    && extensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    && nowUtc - file.LastWriteTimeUtc >= PartialDownloadMinimumAge)
                {
                    RemoveFile(file, removed);
                }
            }
        }
    }

    // Deletes the files in mirror that the folder in use has as well, at the same relative path and size, then the
    // folders that leaves empty. A file only the mirror has stays: it may be a module removed from the folder in use
    // since, which the mirror is then the only copy of.
    private static void RemoveCopies(string directoryInUse, string mirror, Tally removed)
    {
        if (!Directory.Exists(mirror)
            || !Directory.Exists(directoryInUse)
            || IsSameOrWithin(directoryInUse, mirror)
            || IsSameOrWithin(mirror, directoryInUse))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(mirror, "*", SearchOption.AllDirectories).ToList())
        {
            var copy = new FileInfo(file);
            var original = new FileInfo(Path.Combine(directoryInUse, Path.GetRelativePath(mirror, file)));
            if (original.Exists && original.Length == copy.Length)
            {
                RemoveFile(copy, removed);
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(mirror, "*", SearchOption.AllDirectories)
                     .Append(mirror)
                     .OrderByDescending(directory => directory.Length)
                     .ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RemoveFolderIfOnly(string directory, Func<FileSystemInfo, bool> isTheApps, Tally removed)
    {
        var folder = new DirectoryInfo(directory);
        if (!folder.Exists || !folder.EnumerateFileSystemInfos().All(isTheApps))
        {
            return;
        }

        var (filesBefore, bytesBefore) = Measure(folder);
        try
        {
            folder.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A DLL an older version still has loaded, say. What did go is counted; the rest goes next launch.
        }

        folder.Refresh();
        var (filesAfter, bytesAfter) = folder.Exists ? Measure(folder) : (0, 0L);
        removed.Add(filesBefore - filesAfter, bytesBefore - bytesAfter);
    }

    private static void RemoveFile(FileInfo file, Tally removed)
    {
        try
        {
            if (!file.Exists)
            {
                return;
            }

            long length = file.Length;
            file.Delete();
            removed.Add(1, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or locked by a scanner: the next launch tries again.
        }
    }

    private static (int Files, long Bytes) Measure(DirectoryInfo folder)
    {
        int files = 0;
        long bytes = 0;
        foreach (FileInfo file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            files++;
            bytes += file.Length;
        }

        return (files, bytes);
    }

    // The temporary names of a download: ArtifactDownloader's .part, and the .tmp of the downloader before it.
    private static string WithoutDownloadSuffix(string name)
    {
        foreach (string suffix in new[] { ".part", ".tmp" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    private static IEnumerable<string> DistinctExisting(params string[] directories) =>
        directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            .DistinctBy(Normalize, StringComparer.OrdinalIgnoreCase);

    private static bool IsSameOrWithin(string path, string directory)
    {
        string full = Normalize(path);
        string parent = Normalize(directory);
        return full.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed class Tally
    {
        public int Files { get; private set; }

        public long Bytes { get; private set; }

        public void Add(int files, long bytes)
        {
            Files += files;
            Bytes += bytes;
        }
    }
}
