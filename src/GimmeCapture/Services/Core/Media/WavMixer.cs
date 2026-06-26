using System;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Deterministic PCM mixing helpers used when finalizing a recording that captured both system audio
/// and the microphone. Unlike NAudio's <see cref="NAudio.Wave.SampleProviders.MixingSampleProvider"/>
/// (whose post-EOF silence emission makes <c>WaveFileWriter</c> termination format-dependent), this
/// sums the two sources explicitly so the result length and termination are predictable and testable.
/// </summary>
internal static class WavMixer
{
    /// <summary>
    /// Mixes two equal-format interleaved float blocks into <paramref name="dest"/>: sums sample-by-sample
    /// with <paramref name="bGain"/> applied to the second source, clipping each result to [-1, 1].
    /// The two inputs may have different lengths; the shorter is treated as silence beyond its end, so the
    /// number of mixed samples is <c>max(aCount, bCount)</c>. Returns the count written into dest.
    /// </summary>
    public static int MixBlock(float[] a, int aCount, float[] b, int bCount, float bGain, float[] dest)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(dest);

        int n = Math.Max(aCount, bCount);
        if (dest.Length < n)
        {
            throw new ArgumentException("Destination buffer is too small for the mixed block.", nameof(dest));
        }

        for (int i = 0; i < n; i++)
        {
            float s = 0f;
            if (i < aCount) s += a[i];
            if (i < bCount) s += b[i] * bGain;
            dest[i] = Math.Clamp(s, -1f, 1f);
        }

        return n;
    }
}

/// <summary>
/// An <see cref="ISampleProvider"/> that mixes two same-format sources, applying a gain to the second.
/// It reads from both each pull, sums via <see cref="WavMixer.MixBlock"/>, and returns 0 only once BOTH
/// inputs are exhausted — so <c>WaveFileWriter.CreateWaveFile16</c> terminates deterministically at the
/// length of the longer source (the shorter source's tail is mixed as silence). This replaces the
/// <c>MixingSampleProvider { ReadFully = false }</c> workaround whose termination relied on NAudio
/// internals.
/// </summary>
internal sealed class DeterministicMixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _a;
    private readonly ISampleProvider _b;
    private readonly float _bGain;
    private float[] _aBuf = Array.Empty<float>();
    private float[] _bBuf = Array.Empty<float>();
    private float[] _mixBuf = Array.Empty<float>();
    private bool _aDone;
    private bool _bDone;

    public DeterministicMixSampleProvider(ISampleProvider a, ISampleProvider b, float bGain)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!a.WaveFormat.Equals(b.WaveFormat))
        {
            throw new ArgumentException("Both sources must share the same wave format before mixing.");
        }

        _a = a;
        _b = b;
        _bGain = bGain;
        WaveFormat = a.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_aBuf.Length < count) _aBuf = new float[count];
        if (_bBuf.Length < count) _bBuf = new float[count];
        if (_mixBuf.Length < count) _mixBuf = new float[count];

        int aRead = _aDone ? 0 : ReadFull(_a, _aBuf, count, ref _aDone);
        int bRead = _bDone ? 0 : ReadFull(_b, _bBuf, count, ref _bDone);

        int n = Math.Max(aRead, bRead);
        if (n == 0)
        {
            return 0; // both sources exhausted -> CreateWaveFile16 stops
        }

        WavMixer.MixBlock(_aBuf, aRead, _bBuf, bRead, _bGain, _mixBuf);
        Array.Copy(_mixBuf, 0, buffer, offset, n);
        return n;
    }

    // Reads until the scratch buffer holds `count` samples or the source reaches EOF (Read returns 0),
    // so a short result reliably means end-of-stream rather than a mid-stream partial read — without
    // which a partial read would be mixed as silence and punch gaps into the output.
    private static int ReadFull(ISampleProvider src, float[] buf, int count, ref bool done)
    {
        int total = 0;
        while (total < count)
        {
            int r = src.Read(buf, total, count - total);
            if (r == 0)
            {
                done = true;
                break;
            }
            total += r;
        }
        return total;
    }
}
