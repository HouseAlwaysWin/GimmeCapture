using System;
using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// What the ONNX Runtime sessions share. Every model runs on ONNX Runtime's CPU provider: OCR since 2026-10-29,
/// background removal (U2Net) and smart selection (SAM2) since 2026-10-31, and the Windows build has shipped the CPU
/// package since. They ran on DirectML, the GPU provider of the package it shipped before, which does not give back all
/// it allocates: after OCR's first use the process kept about 270 MB until it closed, after U2Net's 255 MB and after
/// SAM2's 370-380 MB, and SAM2 took it up by 2.9 GB while it ran. See the remarks on each service's session builder for
/// what the CPU costs in time.
/// </summary>
internal static class OnnxProviderConfigurator
{
    /// <summary>The provider every session runs on, as the load logs name it.</summary>
    public const string CpuProvider = "CPU";

    /// <summary>
    /// Builds two sessions that only work together, SAM2's encoder and decoder, from the same options. When the second
    /// fails, the first is disposed before the failure goes on, so a load that fails halfway leaves no model behind.
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
