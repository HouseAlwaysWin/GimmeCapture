using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using GimmeCapture.Models;
using Tokenizers.DotNet;

namespace GimmeCapture.Services.Core;

public class MarianMTService : IDisposable
{
    private readonly AIResourceService _aiResourceService;
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    private Tokenizer? _tokenizer;
    
    private int _padTokenId = 0;
    private int _eosTokenId = 2;
    private int _bosTokenId = 1;
    private int _zhTokenId = 250025; // Default for M2M100 Chinese
    private int _jaTokenId = 250012; // Default for M2M100 Japanese

    public MarianMTService(AIResourceService aiResourceService)
    {
        _aiResourceService = aiResourceService;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_encoderSession != null && _decoderSession != null && _tokenizer != null) return;

        await _aiResourceService.EnsureNmtAsync(ct);
        var paths = _aiResourceService.GetNmtPaths();

        if (!File.Exists(paths.Encoder) || !File.Exists(paths.Decoder) || !File.Exists(paths.Tokenizer))
        {
            throw new FileNotFoundException($"MarianMT model files not found: {paths.Encoder}");
        }

        var options = new SessionOptions();
        try { options.AppendExecutionProvider_CUDA(0); } catch { }
        try { options.AppendExecutionProvider_DML(0); } catch { }

        _encoderSession = new InferenceSession(paths.Encoder, options);
        _decoderSession = new InferenceSession(paths.Decoder, options);
        _tokenizer = new Tokenizer(paths.Tokenizer);

        LoadConfig(paths.Config);
    }

    private void LoadConfig(string configPath)
    {
        if (!File.Exists(configPath)) return;
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pad_token_id", out var pad)) _padTokenId = pad.GetInt32();
            if (doc.RootElement.TryGetProperty("eos_token_id", out var eos)) _eosTokenId = eos.GetInt32();
            if (doc.RootElement.TryGetProperty("bos_token_id", out var bos)) _bosTokenId = bos.GetInt32();
            
            _zhTokenId = GetTokenId("__zh__", 250025);
            _jaTokenId = GetTokenId("__ja__", 250012);
        }
        catch { }
    }

    private int GetTokenId(string token, int fallback)
    {
        try
        {
            var ids = _tokenizer?.Encode(token);
            if (ids != null && ids.Length > 0) return (int)ids[0];
        }
        catch { }
        return fallback;
    }

    public async Task<string> TranslateAsync(string text, TranslationLanguage target, CancellationToken ct = default)
    {
        try
        {
            await EnsureLoadedAsync(ct);
            if (_encoderSession == null || _decoderSession == null || _tokenizer == null) return text;

            // [FIX] Split by newlines to handle multi-line text correctly
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            var translatedLines = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    translatedLines.Add(line);
                    continue;
                }

                var ids = _tokenizer.Encode(line);
                var inputIds = new List<long> { _jaTokenId }; 
                inputIds.AddRange(ids.Select(id => (long)id));
                inputIds.Add(_eosTokenId);

                var encoderInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds.ToArray(), new[] { 1, inputIds.Count })),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, inputIds.Count).ToArray(), new[] { 1, inputIds.Count }))
                };

                using var encoderResults = _encoderSession.Run(encoderInputs);
                var lastHiddenState = encoderResults.First().AsTensor<float>();

                var outputIds = new List<long> { _padTokenId, _zhTokenId }; 
                
                // [FIX] Increased maxLen from ~128 to fixed 512 to support longer lines
                int maxLen = 512; 
                
                for (int i = 0; i < maxLen; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var decoderInputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(outputIds.ToArray(), new[] { 1, outputIds.Count })),
                        NamedOnnxValue.CreateFromTensor("encoder_hidden_states", lastHiddenState)
                    };

                    using var decoderResults = _decoderSession.Run(decoderInputs);
                    var logitsTensor = decoderResults.First().AsTensor<float>();
                    
                    int seqLen = (int)logitsTensor.Dimensions[1];
                    int vocabSize = (int)logitsTensor.Dimensions[2];
                    
                    int nextToken = 0;
                    float maxLogit = float.MinValue;
                    
                    // Greedy Search
                    for (int v = 0; v < vocabSize; v++)
                    {
                        float logit = logitsTensor[0, seqLen - 1, v];
                        if (logit > maxLogit)
                        {
                            maxLogit = logit;
                            nextToken = v;
                        }
                    }

                    if (nextToken == _eosTokenId) break;
                    outputIds.Add(nextToken);
                    // Safe break to prevent infinite loops even if maxLen is high
                    if (outputIds.Count > 512) break;
                }

                var actualOutputIds = outputIds.Skip(2).Select(id => (uint)id).ToArray();
                if (actualOutputIds.Length > 0)
                {
                    translatedLines.Add(_tokenizer.Decode(actualOutputIds).Trim());
                }
                else
                {
                    translatedLines.Add(line); // Fallback to original if translation is empty
                }
            }

            // Join back with newlines
            return string.Join("\n", translatedLines);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarianMT] Translation failed: {ex.Message}");
            return text;
        }
    }

    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
        _tokenizer?.Dispose();
    }
}
