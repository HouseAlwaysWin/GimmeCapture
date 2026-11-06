using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using GimmeCapture.Services.Core.Infrastructure;
using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// Copies of ONNX models that ONNX Runtime has already optimized, so that a session builds without optimizing the graph
/// again. OCR's detector took 163-178 ms to build from the model as shipped and 113-118 from its optimized copy with
/// optimization off, the recognizer 87 and 37; a detection or a recognition then took as long from either, with the
/// same output. A copy is made with every optimization on, the processor's own layout included, so it fits the
/// processor, the execution providers and the ONNX Runtime build it was made with: its name carries all three, and the
/// model file's size and time, so a copy that no longer fits is never loaded, and goes once its replacement is saved.
/// Copies sit in an <see cref="FolderName"/> folder beside their model, and go with the module's folder when it is
/// removed.
/// </summary>
internal static class OnnxOptimizedModels
{
    internal const string FolderName = "optimized";

    private const string Suffix = ".optimized.onnx";

    // A copy being written has this after its name until it is complete: a process that died mid-write left it.
    private const string PartialSuffix = ".tmp";
    private static readonly TimeSpan PartialMinimumAge = TimeSpan.FromHours(1);

    /// <summary>
    /// A session for <paramref name="modelPath"/>, from its optimized copy when there is one that fits; else from the
    /// model, optimized as it loads, as before. A copy that fails to load is deleted on the way.
    /// </summary>
    /// <param name="newOptions">The session's options, every optimization setting aside, which this sets.</param>
    /// <param name="providers">
    /// The execution providers <paramref name="newOptions"/> adds, as the copy is fitted to them: OCR's, the CPU alone.
    /// </param>
    /// <param name="fromCopy">Whether the session came from the copy.</param>
    internal static InferenceSession CreateSession(
        string modelPath, Func<SessionOptions> newOptions, string providers, out bool fromCopy)
    {
        string? copy = CopyPathFor(modelPath, providers);
        if (copy != null && File.Exists(copy))
        {
            try
            {
                using var options = newOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
                var session = new InferenceSession(copy, options);
                fromCopy = true;
                return session;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("OnnxOptimizedModels.Load", ex);
                TryDelete(copy);
            }
        }

        using var shippedOptions = newOptions();
        shippedOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        fromCopy = false;
        return new InferenceSession(modelPath, shippedOptions);
    }

    /// <summary>
    /// Saves the optimized copy of <paramref name="modelPath"/> unless one that fits is already there, through a session
    /// built for the purpose and thrown away: 230-340 ms of the processor for OCR's models, so the caller runs it while
    /// the app is idle. Returns whether a copy was saved; a failure is logged and leaves no file behind.
    /// </summary>
    internal static bool EnsureSaved(string modelPath, Func<SessionOptions> newOptions, string providers)
    {
        string? copy = CopyPathFor(modelPath, providers);
        if (copy == null || File.Exists(copy))
        {
            return false;
        }

        // Written under a name of its own and moved into place once complete, so a load never meets half a file.
        string written = copy + "." + Guid.NewGuid().ToString("N") + PartialSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            using (var options = newOptions())
            {
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.OptimizedModelFilePath = written;
                using (new InferenceSession(modelPath, options))
                {
                }
            }

            File.Move(written, copy, overwrite: true);
            RemoveOtherCopies(modelPath, copy);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warning("OnnxOptimizedModels.Save", ex);
            return false;
        }
        finally
        {
            TryDelete(written);
        }
    }

    /// <summary>
    /// Where the copy of <paramref name="modelPath"/> goes: its model's name and a key over the model's full path, size
    /// and time, the execution providers, the ONNX Runtime build and the processor. Null for a model that cannot be read.
    /// </summary>
    internal static string? CopyPathFor(string modelPath, string providers)
    {
        FileInfo model;
        try
        {
            model = new FileInfo(modelPath);
            if (!model.Exists || model.DirectoryName is not { } folder)
            {
                return null;
            }

            string identity = string.Join(
                "|",
                model.FullName.ToUpperInvariant(),
                model.Length,
                model.LastWriteTimeUtc.Ticks,
                providers,
                OrtEnv.Instance().GetVersionString(),
                Processor);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)), 0, 8).ToLowerInvariant();
            return Path.Combine(folder, FolderName, Path.GetFileNameWithoutExtension(model.Name) + "." + key + Suffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // What the optimizer fits a graph to: the architecture, and on x86 the processor and the vector instructions its
    // layout and kernels are chosen by.
    private static readonly string Processor = string.Join(
        ",",
        RuntimeInformation.ProcessArchitecture,
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "",
        Avx2.IsSupported ? "avx2" : "",
        Fma.IsSupported ? "fma" : "",
        Avx512F.IsSupported ? "avx512f" : "",
        Avx512BW.IsSupported ? "avx512bw" : "",
        Avx512Vbmi.IsSupported ? "avx512vbmi" : "",
        AvxVnni.IsSupported ? "avxvnni" : "");

    // Copies of the same model under other keys (an earlier model file, ONNX Runtime build or processor), and copies
    // a process died writing.
    private static void RemoveOtherCopies(string modelPath, string keep)
    {
        string folder = Path.GetDirectoryName(keep)!;
        string prefix = Path.GetFileNameWithoutExtension(modelPath) + ".";
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, prefix + "*"))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // prefix.<16 hex>.optimized.onnx only: another model may be named prefix.something.
                bool otherCopy = name.EndsWith(Suffix, StringComparison.Ordinal)
                    && name.Length == prefix.Length + 16 + Suffix.Length;
                bool partial = name.EndsWith(PartialSuffix, StringComparison.Ordinal)
                    && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= PartialMinimumAge;
                if (otherCopy || partial)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("OnnxOptimizedModels.RemoveOtherCopies", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("OnnxOptimizedModels.Delete", ex);
        }
    }
}
