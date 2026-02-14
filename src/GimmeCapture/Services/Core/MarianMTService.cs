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
    private Microsoft.ML.Tokenizers.SentencePieceTokenizer? _spmTokenizer;
    
    // Vocab from tokenizer.json for correct ID mapping (token string ↔ model ID)
    private Dictionary<string, int>? _vocab;       // token → model_id
    private Dictionary<int, string>? _reverseVocab; // model_id → token
    
    private int _padTokenId = 1;
    private int _eosTokenId = 2;
    private int _bosTokenId = 0;
    private int _zhTokenId = 128102; // __zh__ from tokenizer.json
    private int _jaTokenId = 128046; // __ja__ from tokenizer.json
    private int _enTokenId = 128022; // __en__ from tokenizer.json
    private int _koTokenId = 128052; // __ko__ from tokenizer.json

    public MarianMTService(AIResourceService aiResourceService)
    {
        _aiResourceService = aiResourceService;
    }

    public virtual async Task EnsureLoadedAsync(CancellationToken ct = default)
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
        try
        {
            _encoderSession = new InferenceSession(paths.Encoder, options);
            _decoderSession = new InferenceSession(paths.Decoder, options);
            
            using var stream = File.OpenRead(paths.Spm);
            _spmTokenizer = Microsoft.ML.Tokenizers.SentencePieceTokenizer.Create(stream);

            LoadVocab(paths.Tokenizer);
            LoadConfig(paths.Config);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarianMT] Failed to load ONNX sessions or tokenizer: {ex.Message}");
            throw;
        }
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

            Debug.WriteLine($"[MarianMT] Loaded vocab: {_vocab.Count} entries.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MarianMT] Failed to load vocab: {ex.Message}");
            throw;
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
                if (_vocab.TryGetValue("__ja__", out var jaId)) _jaTokenId = jaId;
                if (_vocab.TryGetValue("__en__", out var enId)) _enTokenId = enId;
                if (_vocab.TryGetValue("__ko__", out var koId)) _koTokenId = koId;
                if (_vocab.TryGetValue("__zh__", out var zhId)) _zhTokenId = zhId;
            }
            Debug.WriteLine($"[MarianMT] Config: pad={_padTokenId}, eos={_eosTokenId}, bos={_bosTokenId}, ja={_jaTokenId}, en={_enTokenId}, ko={_koTokenId}, zh={_zhTokenId}");
        }
        catch { }
    }

    /// <summary>
    /// Encode text to model token IDs using the official Tokenizer.
    /// </summary>
    private List<int> EncodeText(string text)
    {
        if (_spmTokenizer == null || _vocab == null) return new List<int>();
        
        var tokens = _spmTokenizer.EncodeToTokens(text, out _, false, false);
        var modelIds = new List<int>();
        
        for (int i = 0; i < tokens.Count; i++)
        {
            var tokenStr = tokens[i].Value;
            
            // Rejoin logic: if current is lone space prefix and next is word, check combined vocab
            if (tokenStr == "\u2581" && i + 1 < tokens.Count)
            {
                var combined = "\u2581" + tokens[i+1].Value;
                if (_vocab.TryGetValue(combined, out int combinedId))
                {
                    modelIds.Add(combinedId);
                    i++; // skip next
                    continue;
                }
            }

            if (_vocab.TryGetValue(tokenStr, out int modelId))
            {
                modelIds.Add(modelId);
            }
            else
            {
                // Truly unknown
                modelIds.Add(3);
                Debug.WriteLine($"[MarianMT] Token not in vocab: '{tokenStr}'");
            }
        }
        
        return modelIds;
    }

    private string DecodeIds(IEnumerable<int> ids)
    {
        if (_reverseVocab == null) return string.Empty;
        var tokens = ids.Select(id => _reverseVocab.TryGetValue(id, out var t) ? t : "<unk>");
        var text = string.Join("", tokens);
        return text.Replace("\u2581", " ").Trim();
    }

    /// <summary>
    /// Check if text contains any CJK (Chinese/Japanese/Korean) characters.
    /// Used to skip pure-Latin lines that don't need translation.
    /// </summary>
    private static bool HasCjk(string text)
    {
        foreach (char c in text)
        {
            // CJK Unified Ideographs, Hiragana, Katakana, Hangul
            if ((c >= '\u4E00' && c <= '\u9FFF') ||  // CJK Unified Ideographs
                (c >= '\u3040' && c <= '\u309F') ||  // Hiragana
                (c >= '\u30A0' && c <= '\u30FF') ||  // Katakana
                (c >= '\uAC00' && c <= '\uD7A3') ||  // Hangul
                (c >= '\u3400' && c <= '\u4DBF') ||  // CJK Extension A
                (c >= '\uF900' && c <= '\uFAFF'))    // CJK Compatibility
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detect the language of a line by analyzing its characters.
    /// Returns the M2M100 source language token ID.
    /// Priority: Hiragana/Katakana → ja, Hangul → ko, CJK Ideographs → zh, otherwise → en
    /// </summary>
    private int DetectLineLang(string text)
    {
        bool hasHiraganaKatakana = false;
        bool hasHangul = false;
        bool hasCjkIdeograph = false;

        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || // Hiragana
                (c >= '\u30A0' && c <= '\u30FF'))    // Katakana
            {
                hasHiraganaKatakana = true;
            }
            else if (c >= '\uAC00' && c <= '\uD7A3') // Hangul
            {
                hasHangul = true;
            }
            else if ((c >= '\u4E00' && c <= '\u9FFF') || // CJK Unified Ideographs
                     (c >= '\u3400' && c <= '\u4DBF') || // CJK Extension A
                     (c >= '\uF900' && c <= '\uFAFF'))   // CJK Compatibility
            {
                hasCjkIdeograph = true;
            }
        }

        // Hiragana/Katakana is unique to Japanese
        if (hasHiraganaKatakana) return _jaTokenId;
        // Hangul is unique to Korean
        if (hasHangul) return _koTokenId;
        // CJK Ideographs without Japanese/Korean markers → Chinese
        if (hasCjkIdeograph) return _zhTokenId;
        // No CJK at all → English
        return _enTokenId;
    }

    /// <summary>
    /// Map OCRLanguage to M2M100 source language token ID.
    /// Returns -1 for Auto (per-line detection).
    /// </summary>
    private int GetSourceLangToken(OCRLanguage source)
    {
        return source switch
        {
            OCRLanguage.Japanese => _jaTokenId,
            OCRLanguage.English => _enTokenId,
            OCRLanguage.Korean => _koTokenId,
            OCRLanguage.TraditionalChinese => _zhTokenId,
            OCRLanguage.SimplifiedChinese => _zhTokenId,
            _ => -1 // Auto: per-line detection
        };
    }

    private int GetTargetLangToken(TranslationLanguage target)
    {
        return target switch
        {
            TranslationLanguage.Japanese => _jaTokenId,
            TranslationLanguage.English => _enTokenId,
            TranslationLanguage.Korean => _koTokenId, // Should not happen if UI filtered correctly, but safe fallback
            TranslationLanguage.TraditionalChinese => _zhTokenId,
            TranslationLanguage.SimplifiedChinese => _zhTokenId,
            _ => _zhTokenId
        };
    }

    public virtual async Task<string?> TranslateAsync(string text, TranslationLanguage target, OCRLanguage source = OCRLanguage.Auto, CancellationToken ct = default)
    {
        try
        {
            await EnsureLoadedAsync(ct);
            if (_encoderSession == null || _decoderSession == null || _spmTokenizer == null || _vocab == null) return text;

            Debug.WriteLine($"[MarianMT] === Translation Start ===");
            Debug.WriteLine($"[MarianMT] Input: '{text}', source={source}");

            // Clean OCR artifacts: <unk0>, <unk1>, etc. → spaces
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<unk\d*>", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ");

            // Determine fixed source language token (or -1 for auto-detection)
            int fixedSourceToken = GetSourceLangToken(source);
            // Check if fixed source is a CJK language (ja/ko/zh)
            bool sourceIsCjkLanguage = source == OCRLanguage.Japanese || source == OCRLanguage.Korean ||
                                       source == OCRLanguage.TraditionalChinese || source == OCRLanguage.SimplifiedChinese;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            var translatedLines = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    translatedLines.Add(line);
                    continue;
                }

                // Determine source language token for this line
                int sourceLangToken;
                if (fixedSourceToken > 0)
                {
                    // Explicit source language: skip lines that don't match
                    bool lineHasCjk = HasCjk(line);
                    if (sourceIsCjkLanguage && !lineHasCjk)
                    {
                        // Source is CJK language but line is pure Latin → skip
                        Debug.WriteLine($"[MarianMT] Skip non-CJK line (source={source}): '{line}'");
                        translatedLines.Add(line.Trim());
                        continue;
                    }
                    if (!sourceIsCjkLanguage && lineHasCjk)
                    {
                        // Source is English but line is CJK → skip
                        Debug.WriteLine($"[MarianMT] Skip CJK line (source={source}): '{line}'");
                        translatedLines.Add(line.Trim());
                        continue;
                    }
                    sourceLangToken = fixedSourceToken;
                }
                else
                {
                    // Auto-detect per line: ja/ko/zh/en
                    sourceLangToken = DetectLineLang(line);
                    string langName = sourceLangToken == _jaTokenId ? "ja" :
                                      sourceLangToken == _koTokenId ? "ko" :
                                      sourceLangToken == _zhTokenId ? "zh" :
                                      sourceLangToken == _enTokenId ? "en" : "??";
                    Debug.WriteLine($"[MarianMT] Auto-detect: {langName}");
                }
                Debug.WriteLine($"[MarianMT] Line src={sourceLangToken}: '{line}'");

                var tokenIds = EncodeText(line);
                Debug.WriteLine($"[MarianMT] Encoded -> {tokenIds.Count} model IDs: [{string.Join(",", tokenIds.Take(20))}]");

                // Build encoder input: [source_lang_token, ...token_ids, eos]
                var inputIds = new List<long> { sourceLangToken };
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
                int targetLangToken = GetTargetLangToken(target);
                var outputIds = new List<long> { _eosTokenId, targetLangToken };
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
                    
                    // Early termination: detect loops
                    // Check for 3+ consecutive identical tokens (e.g., 3,3,3)
                    if (outputIds.Count >= 2 && outputIds[^1] == nextToken && outputIds[^2] == nextToken)
                    {
                        Debug.WriteLine($"[MarianMT] Repeated token loop at step {i}, stopping.");
                        break;
                    }
                    // Check for alternating 2-token pattern (e.g., 22,3,22,3)
                    if (outputIds.Count >= 3 && outputIds[^2] == nextToken && outputIds[^1] == outputIds[^3])
                    {
                        Debug.WriteLine($"[MarianMT] Alternating token loop at step {i}, stopping.");
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
                    // Clean up <unk> tokens from output
                    decoded = decoded.Replace("<unk>", "");
                    decoded = System.Text.RegularExpressions.Regex.Replace(decoded, @"\s{2,}", " ");
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
