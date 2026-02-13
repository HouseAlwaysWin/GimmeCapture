using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using GimmeCapture.Models;
using Microsoft.ML.Tokenizers;

namespace GimmeCapture.Services.Core;

public class MarianMTService : IDisposable
{
    private readonly AIResourceService _aiResourceService;
    private InferenceSession? _encoderSession;
    private InferenceSession? _decoderSession;
    
    // SPM tokenizer for subword splitting (text → token strings)
    private Tokenizer? _spmTokenizer;
    
    // Vocab from tokenizer.json for correct ID mapping (token string ↔ model ID)
    private Dictionary<string, int>? _vocab;       // token → model_id
    private Dictionary<int, string>? _reverseVocab; // model_id → token
    
    private int _padTokenId = 1;
    private int _eosTokenId = 2;
    private int _bosTokenId = 0;
    private int _zhTokenId = 128102; // __zh__ from tokenizer.json
    private int _jaTokenId = 128046; // __ja__ from tokenizer.json

    public MarianMTService(AIResourceService aiResourceService)
    {
        _aiResourceService = aiResourceService;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_encoderSession != null && _decoderSession != null && _spmTokenizer != null && _vocab != null) return;

        await _aiResourceService.EnsureNmtAsync(ct);
        var paths = _aiResourceService.GetNmtPaths();

        if (!File.Exists(paths.Encoder) || !File.Exists(paths.Decoder) || !File.Exists(paths.Spm) || !File.Exists(paths.Tokenizer))
        {
            throw new FileNotFoundException("MarianMT model files not found.");
        }

        var options = new SessionOptions();
        try { options.AppendExecutionProvider_CUDA(0); } catch { }
        try { options.AppendExecutionProvider_DML(0); } catch { }

        _encoderSession = new InferenceSession(paths.Encoder, options);
        _decoderSession = new InferenceSession(paths.Decoder, options);
        
        // Load SPM for subword splitting only (text → token strings)
        try 
        {
            using var stream = File.OpenRead(paths.Spm);
            _spmTokenizer = SentencePieceTokenizer.Create(stream);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarianMT] Failed to load SentencePiece tokenizer: {ex.Message}");
            throw;
        }

        // Load vocab from tokenizer.json for correct model ID mapping
        LoadVocab(paths.Tokenizer);
        LoadConfig(paths.Config);
    }

    /// <summary>
    /// Load the full vocabulary from tokenizer.json for correct SPM token → model ID mapping.
    /// The tokenizer.json "model.vocab" contains the exact mapping the ONNX model expects.
    /// </summary>
    private void LoadVocab(string tokenizerJsonPath)
    {
        try
        {
            var json = File.ReadAllText(tokenizerJsonPath);
            var doc = JsonNode.Parse(json);
            var vocabNode = doc?["model"]?["vocab"]?.AsObject();
            
            if (vocabNode == null)
            {
                Debug.WriteLine("[MarianMT] No vocab found in tokenizer.json");
                return;
            }

            _vocab = new Dictionary<string, int>(vocabNode.Count);
            _reverseVocab = new Dictionary<int, string>(vocabNode.Count);

            foreach (var kv in vocabNode)
            {
                int id = kv.Value!.GetValue<int>();
                _vocab[kv.Key] = id;
                _reverseVocab[id] = kv.Key;
            }

            Debug.WriteLine($"[MarianMT] Loaded vocab: {_vocab.Count} entries");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarianMT] Failed to load vocab: {ex.Message}");
        }
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
            
            // Verify language token IDs from vocab if loaded
            if (_vocab != null)
            {
                if (_vocab.TryGetValue("__zh__", out var zhId)) _zhTokenId = zhId;
                if (_vocab.TryGetValue("__ja__", out var jaId)) _jaTokenId = jaId;
            }
            Debug.WriteLine($"[MarianMT] Config: pad={_padTokenId}, eos={_eosTokenId}, bos={_bosTokenId}, ja={_jaTokenId}, zh={_zhTokenId}");
        }
        catch { }
    }

    /// <summary>
    /// Encode text to model token IDs using SPM subword splitting + tokenizer.json vocab mapping.
    /// SPM splits text into subword strings, then each string is looked up in the vocab for
    /// the correct model ID that the ONNX model expects.
    /// </summary>
    private List<int> EncodeText(string text)
    {
        if (_spmTokenizer == null || _vocab == null) return new List<int>();
        
        // Use SPM EncodeToTokens to get subword token strings
        var tokens = _spmTokenizer.EncodeToTokens(text, out _, false, false);
        var modelIds = new List<int>();
        
        foreach (var token in tokens)
        {
            var tokenStr = token.Value;
            
            // Skip BOS/EOS control tokens added by SPM
            if (tokenStr == "<s>" || tokenStr == "</s>") continue;
            
            if (_vocab.TryGetValue(tokenStr, out int modelId))
            {
                modelIds.Add(modelId);
            }
            else
            {
                // Unknown token - map to <unk> (ID=3)
                modelIds.Add(3);
                Debug.WriteLine($"[MarianMT] Token not in vocab: '{tokenStr}'");
            }
        }
        
        return modelIds;
    }

    /// <summary>
    /// Decode model token IDs back to text using tokenizer.json reverse vocab.
    /// </summary>
    private string DecodeIds(IEnumerable<int> ids)
    {
        if (_reverseVocab == null) return "";
        
        var tokens = new List<string>();
        foreach (var id in ids)
        {
            if (_reverseVocab.TryGetValue(id, out var token))
            {
                tokens.Add(token);
            }
        }
        
        // SentencePiece uses ▁ (U+2581) as word separator prefix
        var text = string.Join("", tokens);
        text = text.Replace("\u2581", " ");
        return text;
    }

    public async Task<string> TranslateAsync(string text, TranslationLanguage target, CancellationToken ct = default)
    {
        try
        {
            await EnsureLoadedAsync(ct);
            if (_encoderSession == null || _decoderSession == null || _spmTokenizer == null || _vocab == null) return text;

            Debug.WriteLine($"[MarianMT] === Translation Start ===");
            Debug.WriteLine($"[MarianMT] Input: '{text}'");

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            var translatedLines = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    translatedLines.Add(line);
                    continue;
                }

                var tokenIds = EncodeText(line);
                Debug.WriteLine($"[MarianMT] Encoded '{line}' -> {tokenIds.Count} model IDs: [{string.Join(",", tokenIds.Take(20))}]");

                // Build encoder input: [source_lang_token, ...token_ids, eos]
                var inputIds = new List<long> { _jaTokenId };
                inputIds.AddRange(tokenIds.Select(id => (long)id));
                inputIds.Add(_eosTokenId);
                Debug.WriteLine($"[MarianMT] Encoder input ({inputIds.Count}): [{string.Join(",", inputIds.Take(30))}]");

                var attentionMask = Enumerable.Repeat(1L, inputIds.Count).ToArray();
                var attentionMaskTensor = new DenseTensor<long>(attentionMask, new[] { 1, inputIds.Count });

                var encoderInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds.ToArray(), new[] { 1, inputIds.Count })),
                    NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor)
                };

                using var encoderResults = _encoderSession.Run(encoderInputs);
                var lastHiddenState = encoderResults.First().AsTensor<float>();

                // Decoder seed: [eos, target_lang_token] per M2M100 spec (decoder_start_token_id=2)
                var outputIds = new List<long> { _eosTokenId, _zhTokenId };
                int maxLen = Math.Min(inputIds.Count * 3, 128);
                Debug.WriteLine($"[MarianMT] Decoder seed: [{string.Join(",", outputIds)}], maxLen={maxLen}");

                for (int i = 0; i < maxLen; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var decoderInputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(outputIds.ToArray(), new[] { 1, outputIds.Count })),
                        NamedOnnxValue.CreateFromTensor("encoder_hidden_states", lastHiddenState),
                        NamedOnnxValue.CreateFromTensor("encoder_attention_mask", attentionMaskTensor)
                    };

                    using var decoderResults = _decoderSession.Run(decoderInputs);
                    var logitsTensor = decoderResults.First().AsTensor<float>();
                    
                    int seqLen = (int)logitsTensor.Dimensions[1];
                    int vocabSize = (int)logitsTensor.Dimensions[2];
                    
                    int nextToken = 0;
                    float maxLogit = float.MinValue;
                    
                    // Greedy search: pick the token with highest logit
                    for (int v = 0; v < vocabSize; v++)
                    {
                        float logit = logitsTensor[0, seqLen - 1, v];
                        if (logit > maxLogit)
                        {
                            maxLogit = logit;
                            nextToken = v;
                        }
                    }

                    if (i < 10 || i % 10 == 0) Debug.WriteLine($"[MarianMT] Step {i}: token={nextToken}, logit={maxLogit:F2}");

                    if (nextToken == _eosTokenId)
                    {
                        Debug.WriteLine($"[MarianMT] EOS at step {i}");
                        break;
                    }
                    outputIds.Add(nextToken);
                }

                // Skip the 2 seed tokens (eos, target_lang)
                var resultIds = outputIds.Skip(2).Select(id => (int)id).ToArray();
                Debug.WriteLine($"[MarianMT] Output IDs ({resultIds.Length}): [{string.Join(",", resultIds.Take(30))}]");
                
                if (resultIds.Length > 0)
                {
                    var decoded = DecodeIds(resultIds);
                    Debug.WriteLine($"[MarianMT] Decoded: '{decoded}'");
                    translatedLines.Add(decoded.Trim());
                }
                else
                {
                    translatedLines.Add(line);
                }
            }

            var result = string.Join("\n", translatedLines);
            
            // M2M100 outputs Simplified Chinese; convert to Traditional if needed
            if (target == TranslationLanguage.TraditionalChinese)
            {
                result = ConvertToTraditional(result);
                Debug.WriteLine($"[MarianMT] After S2T: '{result}'");
            }
            
            Debug.WriteLine($"[MarianMT] === Result: '{result}' ===");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarianMT] Translation failed: {ex.Message}");
            return text;
        }
    }

    // Windows LCMapStringEx for Simplified → Traditional Chinese conversion
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName, uint dwMapFlags,
        string lpSrcStr, int cchSrc,
        StringBuilder? lpDestStr, int cchDest,
        IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    /// <summary>
    /// Convert Simplified Chinese text to Traditional Chinese using Windows API.
    /// </summary>
    private static string ConvertToTraditional(string simplified)
    {
        if (string.IsNullOrEmpty(simplified)) return simplified;
        
        try
        {
            int len = LCMapStringEx("zh-TW", LCMAP_TRADITIONAL_CHINESE,
                simplified, simplified.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0) return simplified;
            
            var sb = new StringBuilder(len);
            LCMapStringEx("zh-TW", LCMAP_TRADITIONAL_CHINESE,
                simplified, simplified.Length, sb, len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return sb.ToString();
        }
        catch
        {
            return simplified;
        }
    }

    public void Dispose()
    {
        _encoderSession?.Dispose();
        _decoderSession?.Dispose();
    }
}
