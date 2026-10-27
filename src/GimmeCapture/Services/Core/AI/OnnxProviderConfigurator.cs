using System;
using GimmeCapture.Services.Core.Infrastructure;
using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// Centralizes ONNX Runtime execution-provider setup so the GPU fallback logic lives in one place
/// (previously duplicated across <see cref="OcrRuntimeService"/> and <see cref="SAM2RuntimeService"/>).
/// </summary>
internal static class OnnxProviderConfigurator
{
    /// <summary>The provider <c>CreateWithCpuFallback</c> reports for a session that has no GPU provider in it.</summary>
    public const string CpuProvider = "CPU";

    /// <summary>
    /// Appends GPU execution providers (CUDA then DirectML) to <paramref name="options"/>, falling back
    /// silently to the next provider — and ultimately to CPU — when one is unavailable.
    /// </summary>
    /// <returns>The first provider appended, "CUDA" or "DirectML", or null when neither was and the session will run on
    /// the CPU. The Windows build ships ONNX Runtime's DirectML package and the Linux build its CPU package, so CUDA
    /// never appends in either.</returns>
    /// <remarks>
    /// A missing CUDA/DirectML provider is expected control flow on CPU-only machines, not an error,
    /// so probe failures are intentionally not logged here to avoid misleading per-load warning noise.
    /// </remarks>
    public static string? AppendGpuProvidersWithFallback(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? appended = null;

        try { options.AppendExecutionProvider_CUDA(0); appended = "CUDA"; }
        catch { /* CUDA not available — fall through to DirectML/CPU. */ }

        try { options.AppendExecutionProvider_DML(0); appended ??= "DirectML"; }
        catch { /* DirectML not available — fall through to CPU. */ }

        return appended;
    }

    /// <summary>
    /// Holds the GPU device open until the result is disposed. DirectML closes its device with the last session or
    /// options using it, and opening it again costs about 0.2 s, so a caller replacing its sessions holds one across
    /// the swap: the new sessions then take over the device the old ones had. Holds nothing where no GPU provider
    /// appends.
    /// </summary>
    public static IDisposable HoldGpuDevice()
    {
        var options = new SessionOptions();
        AppendGpuProvidersWithFallback(options);
        return options;
    }

    /// <summary>
    /// Builds a session on the GPU providers and, when one was appended but the session then fails to build, builds it
    /// again on the CPU alone. DirectML can append cleanly and still fail inside the session constructor (Windows 10
    /// with an older DX12 driver). A session that fails with no GPU provider in it is not retried: the CPU attempt
    /// would fail the same way.
    /// </summary>
    /// <param name="createOptions">Fresh options for each attempt, so the CPU attempt carries no GPU provider.</param>
    /// <param name="createSession">Builds the session (or sessions) from the options, which are disposed once it
    /// returns: a session no longer needs them, and DirectML's options hold the GPU device open.</param>
    /// <param name="operation">Log category of the warning a CPU fallback writes.</param>
    /// <param name="provider">What the session runs on: "DirectML", "CUDA" or <see cref="CpuProvider"/>.</param>
    public static T CreateWithCpuFallback<T>(
        Func<SessionOptions> createOptions,
        Func<SessionOptions, T> createSession,
        string operation,
        out string provider)
        => CreateWithCpuFallback(createOptions, createSession, operation, AppendGpuProvidersWithFallback, out provider);

    /// <summary>The overload above with the GPU providers appended by <paramref name="appendGpuProviders"/>, so tests can
    /// stand in for a GPU.</summary>
    internal static T CreateWithCpuFallback<T>(
        Func<SessionOptions> createOptions,
        Func<SessionOptions, T> createSession,
        string operation,
        Func<SessionOptions, string?> appendGpuProviders,
        out string provider)
    {
        using (var gpuOptions = createOptions())
        {
            string? gpuProvider = appendGpuProviders(gpuOptions);
            try
            {
                T session = createSession(gpuOptions);
                provider = gpuProvider ?? CpuProvider;
                return session;
            }
            catch (Exception ex) when (gpuProvider != null)
            {
                AppLog.Warning($"{operation}.GpuSessionCreateFailed.CpuFallback", ex);
            }
        }

        using var cpuOptions = createOptions();
        T cpuSession = createSession(cpuOptions);
        provider = CpuProvider;
        return cpuSession;
    }

    /// <summary>
    /// Builds two sessions that only work together, OCR's detection and recognition or SAM2's encoder and decoder, from
    /// the same options, so both run on the same provider. Given to <c>CreateWithCpuFallback</c> as one factory, a
    /// failure of either on the GPU builds both again on the CPU. When the second fails, the first is disposed before
    /// the failure goes on, so a GPU attempt that fails halfway leaves no session holding the device.
    /// </summary>
    public static (InferenceSession First, InferenceSession Second) CreateSessionPair(
        SessionOptions options,
        string firstModelPath,
        string secondModelPath)
        => CreateSessionPair(
            options,
            pairOptions => new InferenceSession(firstModelPath, pairOptions),
            pairOptions => new InferenceSession(secondModelPath, pairOptions));

    /// <summary>The overload above with the sessions built by <paramref name="createFirst"/> and
    /// <paramref name="createSecond"/>, so tests can stand in for them.</summary>
    internal static (T First, T Second) CreateSessionPair<T>(
        SessionOptions options,
        Func<SessionOptions, T> createFirst,
        Func<SessionOptions, T> createSecond)
        where T : IDisposable
    {
        T first = createFirst(options);
        try
        {
            return (first, createSecond(options));
        }
        catch
        {
            first.Dispose();
            throw;
        }
    }
}
