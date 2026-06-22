using System;
using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// Centralizes ONNX Runtime execution-provider setup so the GPU fallback logic lives in one place
/// (previously duplicated across <see cref="OcrRuntimeService"/> and <see cref="SAM2RuntimeService"/>).
/// </summary>
internal static class OnnxProviderConfigurator
{
    /// <summary>
    /// Appends GPU execution providers (CUDA then DirectML) to <paramref name="options"/>, falling back
    /// silently to the next provider — and ultimately to CPU — when one is unavailable.
    /// </summary>
    /// <remarks>
    /// A missing CUDA/DirectML provider is expected control flow on CPU-only machines, not an error,
    /// so probe failures are intentionally not logged here to avoid misleading per-load warning noise.
    /// </remarks>
    public static void AppendGpuProvidersWithFallback(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try { options.AppendExecutionProvider_CUDA(0); }
        catch { /* CUDA not available — fall through to DirectML/CPU. */ }

        try { options.AppendExecutionProvider_DML(0); }
        catch { /* DirectML not available — fall through to CPU. */ }
    }
}
