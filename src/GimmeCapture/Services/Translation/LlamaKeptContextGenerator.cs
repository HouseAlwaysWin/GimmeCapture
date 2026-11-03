using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Abstractions;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;

namespace GimmeCapture.Services.Translation;

/// <summary>
/// Generates on one context kept from request to request, so a prompt that begins as the last one did is decoded only
/// from where the two part. Every translation of a language pair begins with the same instructions, 72 tokens for
/// TranslateGemma, and reading them again took about 1 s of every request on the CPU, of the 1.2-2.4 s before its
/// first token. StatelessExecutor also built and freed a context for each request, 60-90 ms.
/// <para>
/// The generation is StatelessExecutor's, as LLamaSharp 0.30 has it: the prompt tokenized with BOS and special tokens and
/// decoded in batches, then a token at a time from the request's sampling pipeline until the end of generation, an
/// anti-prompt or MaxTokens. A request that could fill the context, which StatelessExecutor would then shift, is left to
/// StatelessExecutor. The context holds its KV cache (272 MB for TranslateGemma 4B at 2048 tokens) for as long as the
/// model stays loaded, where each request built and freed one; llama.cpp keeps a full-size cache for the
/// sliding-window layers, so no position the next prompt shares has been dropped from it.
/// </para>
/// </summary>
internal sealed class LlamaKeptContextGenerator : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly IContextParams _parameters;
    private readonly LlamaPromptCache _cache = new();
    private readonly LLamaBatch _batch = new();
    private LLamaContext? _context;

    public LlamaKeptContextGenerator(LLamaWeights weights, IContextParams parameters)
    {
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    /// <summary>
    /// The last request, for the log: its prompt's tokens and how many the context kept, then how long each stage took.
    /// </summary>
    public string? LastTimings { get; private set; }

    /// <summary>How many of the last prompt's tokens the context already held.</summary>
    public int LastKept { get; private set; }

    /// <summary>
    /// What <paramref name="prompt"/> generates, as StatelessExecutor generates it; null when the prompt and the most
    /// tokens <paramref name="inference"/> lets it generate do not fit in the context. The caller generates those with
    /// StatelessExecutor, and this context is freed first, so the two are never held at once.
    /// </summary>
    public async Task<string?> TryGenerateAsync(string prompt, IInferenceParams inference, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        bool built = _context == null;
        LLamaContext context = _context ??= _weights.CreateContext(_parameters);
        long buildMs = watch.ElapsedMilliseconds;

        LLamaToken[] tokens = context.Tokenize(prompt, addBos: true, special: true);
        int maxTokens = inference.MaxTokens < 0 ? int.MaxValue : inference.MaxTokens;
        if ((long)tokens.Length + maxTokens >= context.ContextSize)
        {
            ReleaseContext();
            return null;
        }

        int kept = LastKept = KeepSharedTokens(context.NativeHandle, tokens);
        ISamplingPipeline sampling = inference.SamplingPipeline;
        sampling.Reset();
        var decoder = new StreamingTokenDecoder(context) { DecodeSpecialTokens = inference.DecodeSpecialTokens };
        var antiprompts = new AntipromptProcessor(inference.AntiPrompts);
        var text = new StringBuilder();
        long promptMs = 0;
        int generated = 0;
        bool consistent = false;
        try
        {
            long stage = watch.ElapsedMilliseconds;
            var (result, _, past) = await context.DecodeAsync(
                new List<LLamaToken>(tokens[kept..]), LLamaSeqId.Zero, _batch, kept, ct).ConfigureAwait(false);
            if (result != DecodeResult.Ok)
            {
                throw new LLamaDecodeError(result);
            }

            _cache.Append(tokens.AsSpan(kept));
            promptMs = watch.ElapsedMilliseconds - stage;
            int nPast = past;
            for (int i = 0; i < maxTokens && !ct.IsCancellationRequested; i++)
            {
                LLamaToken id = sampling.Sample(context.NativeHandle, _batch.TokenCount - 1);
                if (id.IsEndOfGeneration(_weights.Vocab))
                {
                    break;
                }

                decoder.Add(id);
                string decoded = decoder.Read();
                text.Append(decoded);
                generated++;
                if (antiprompts.Add(decoded))
                {
                    break;
                }

                _batch.Clear();
                _batch.Add(id, nPast++, LLamaSeqId.Zero, true);
                DecodeResult decodedResult = await context.DecodeAsync(_batch, ct).ConfigureAwait(false);
                if (decodedResult != DecodeResult.Ok)
                {
                    throw new LLamaDecodeError(decodedResult);
                }

                _cache.Append(id);
            }

            consistent = true;
        }
        finally
        {
            // A decode that threw or was cancelled leaves the cache holding more than the tokens counted: start over.
            if (!consistent)
            {
                context.NativeHandle.MemoryClear(false);
                _cache.Clear();
            }

            long total = watch.ElapsedMilliseconds;
            LastTimings = string.Create(
                CultureInfo.InvariantCulture,
                $"prompt={tokens.Length} kept={kept} context={(built ? buildMs.ToString(CultureInfo.InvariantCulture) : "kept")} " +
                $"read={promptMs} generated={generated} generate={total - promptMs - buildMs} total={total}ms");
        }

        return text.ToString();
    }

    /// <summary>Frees the context and the tokens it holds; the next request builds another.</summary>
    public void ReleaseContext()
    {
        _context?.Dispose();
        _context = null;
        _cache.Clear();
    }

    public void Dispose() => ReleaseContext();

    // How many of the prompt's first tokens the context keeps: those it holds already, everything after them removed.
    // Should the cache not hold exactly those, positions 0 to kept − 1, it is cleared and the prompt decoded whole.
    private int KeepSharedTokens(SafeLLamaContextHandle handle, ReadOnlySpan<LLamaToken> tokens)
    {
        int kept = _cache.Reusable(tokens);
        if (kept < _cache.Count)
        {
            handle.MemorySequenceRemove(LLamaSeqId.Zero, kept, -1);
            _cache.Truncate(kept);
        }

        if (kept > 0
            && ((int)handle.MemorySequenceMinPosition(LLamaSeqId.Zero) != 0
                || (int)handle.MemorySequenceMaxPosition(LLamaSeqId.Zero) != kept - 1))
        {
            handle.MemoryClear(false);
            _cache.Clear();
            kept = 0;
        }

        return kept;
    }
}
