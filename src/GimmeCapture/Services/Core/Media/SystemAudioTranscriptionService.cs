using GimmeCapture.Models;
using NAudio.Wave;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vosk;

namespace GimmeCapture.Services.Core.Media;

public sealed class SystemAudioTranscriptionService : IDisposable
{
    private const string VoskEnModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
    private const string VoskCnModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-cn-0.22.zip";
    private const string VoskJaModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ja-0.22.zip";
    private const string VoskKoModelUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ko-0.22.zip";

    private readonly string _tempDirectory;
    private readonly string _modelDirectory;
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly HttpClient _httpClient = new();
    private Model? _voskModel;
    private string _loadedModelTag = string.Empty;
    private readonly object _captureLock = new();
    private readonly MemoryStream _captureBuffer = new();
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveFormat? _loopbackFormat;
    private const int MaxBufferedSeconds = 40;
    public string LastStatus { get; private set; } = "Initializing audio transcription...";
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private OCRLanguage _workerLanguage = OCRLanguage.Auto;
    private OCRLanguage _workerAutoPreferredLanguage = OCRLanguage.Auto;
    private string _latestTranscript = string.Empty;
    private OCRLanguage _autoLanguageHint = OCRLanguage.Japanese;
    public OCRLanguage LastDetectedLanguage { get; private set; } = OCRLanguage.Auto;
    private const int MinTranscriptLength = 2;

    private readonly struct TranscriptionResult
    {
        public OCRLanguage Language { get; init; }
        public string Text { get; init; }
        public double Confidence { get; init; }
        public double Score { get; init; }
    }

    public SystemAudioTranscriptionService(string baseDataDirectory)
    {
        _tempDirectory = Path.Combine(baseDataDirectory, "Temp", "AudioTranscription");
        _modelDirectory = Path.Combine(baseDataDirectory, "AIResources", "asr", "vosk");
        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_modelDirectory);
        Vosk.Vosk.SetLogLevel(-1);
    }

    public Task<string> CaptureAndTranscribeAsync(
        TimeSpan duration,
        OCRLanguage sourceLanguage,
        OCRLanguage autoPreferredLanguage = OCRLanguage.Auto,
        CancellationToken ct = default)
    {
        try
        {
            EnsureLoopbackCaptureStarted();
            EnsureWorkerStarted(sourceLanguage, autoPreferredLanguage, duration);
            if (string.IsNullOrWhiteSpace(_latestTranscript))
            {
                if (string.IsNullOrWhiteSpace(LastStatus))
                {
                    LastStatus = "Listening...";
                }
                return Task.FromResult(string.Empty);
            }
            return Task.FromResult(_latestTranscript);
        }
        catch (OperationCanceledException)
        {
            LastStatus = "Transcription cancelled.";
            return Task.FromResult(string.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioSTT] CaptureAndTranscribeAsync failed: {ex.Message}");
            LastStatus = $"Transcription error: {ex.Message}";
            return Task.FromResult(string.Empty);
        }
    }

    private void EnsureWorkerStarted(OCRLanguage sourceLanguage, OCRLanguage autoPreferredLanguage, TimeSpan lookback)
    {
        bool needRestart = _workerTask == null
                           || _workerTask.IsCompleted
                           || _workerLanguage != sourceLanguage
                           || _workerAutoPreferredLanguage != autoPreferredLanguage;
        if (!needRestart) return;

        StopWorker();
        _workerLanguage = sourceLanguage;
        _workerAutoPreferredLanguage = autoPreferredLanguage;
        if (_workerLanguage == OCRLanguage.Auto && _workerAutoPreferredLanguage != OCRLanguage.Auto)
        {
            _autoLanguageHint = _workerAutoPreferredLanguage;
        }
        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => RunWorkerLoopAsync(_workerCts.Token, lookback));
    }

    private async Task RunWorkerLoopAsync(CancellationToken ct, TimeSpan lookback)
    {
        string rawPath = Path.Combine(_tempDirectory, "live_raw.wav");
        string normalizedPath = Path.Combine(_tempDirectory, "live_16k.wav");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!TryWriteRecentAudioToWave(rawPath, lookback))
                    {
                        LastStatus = "No system audio buffered yet.";
                        await Task.Delay(300, ct);
                        continue;
                    }

                    NormalizeForSpeech(rawPath, normalizedPath);
                    if (!File.Exists(normalizedPath) || new FileInfo(normalizedPath).Length <= 44)
                    {
                        LastStatus = "Audio normalization failed.";
                        await Task.Delay(300, ct);
                        continue;
                    }

                    if (IsLikelySilent(normalizedPath, out var db))
                    {
                        LastStatus = $"Audio too low ({db:F1} dB).";
                        await Task.Delay(300, ct);
                        continue;
                    }

                    var result = await TranscribeWithLanguageStrategyAsync(normalizedPath, ct);
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        _latestTranscript = result.Text;
                        LastDetectedLanguage = result.Language;
                        LastStatus = $"Transcription updated ({result.Language}, conf {result.Confidence:F2}).";
                    }
                    else
                    {
                        LastStatus = "Heard audio but no text recognized.";
                    }

                    await Task.Delay(350, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastStatus = $"Worker error: {ex.Message}";
                    await Task.Delay(500, ct);
                }
            }
        }
        finally
        {
            TryDelete(rawPath);
            TryDelete(normalizedPath);
        }
    }

    private async Task<TranscriptionResult> TranscribeWithLanguageStrategyAsync(string normalizedPath, CancellationToken ct)
    {
        if (_workerLanguage != OCRLanguage.Auto)
        {
            var model = await EnsureModelAsync(_workerLanguage, ct);
            if (model == null)
            {
                LastStatus = $"Missing Vosk model for {_workerLanguage}. Install in Modules.";
                return default;
            }

            LastStatus = $"Transcribing ({_workerLanguage})...";
            var direct = await TranscribeWaveFileAsync(normalizedPath, model, ct);
            return new TranscriptionResult
            {
                Language = _workerLanguage,
                Text = direct.Text,
                Confidence = direct.Confidence,
                Score = direct.Confidence
            };
        }

        var autoCandidates = new[]
        {
            _autoLanguageHint,
            OCRLanguage.TraditionalChinese,
            OCRLanguage.SimplifiedChinese,
            OCRLanguage.Japanese,
            OCRLanguage.Korean,
            OCRLanguage.English
        }
            .Where(x => x != OCRLanguage.Auto)
            .Distinct()
            .ToArray();

        bool preferCjk = _workerAutoPreferredLanguage is OCRLanguage.TraditionalChinese
            or OCRLanguage.SimplifiedChinese
            or OCRLanguage.Japanese
            or OCRLanguage.Korean;
        if (preferCjk)
        {
            autoCandidates = autoCandidates.Where(x => x != OCRLanguage.English).ToArray();
        }

        TranscriptionResult best = default;
        TranscriptionResult bestEnglish = default;
        TranscriptionResult bestCjk = default;
        double bestScore = double.MinValue;

        foreach (var lang in autoCandidates)
        {
            var model = await EnsureModelAsync(lang, ct);
            if (model == null) continue;

            LastStatus = $"Auto transcribing ({lang})...";
            var candidate = await TranscribeWaveFileAsync(normalizedPath, model, ct);
            if (!string.IsNullOrWhiteSpace(candidate.Text))
            {
                double score = ScoreAutoCandidate(lang, candidate.Text, candidate.Confidence);
                if (lang == _autoLanguageHint)
                {
                    score += 0.25;
                }

                if (score > bestScore || (Math.Abs(score - bestScore) < 0.001 && candidate.Text.Length > best.Text.Length))
                {
                    bestScore = score;
                    best = new TranscriptionResult
                    {
                        Language = lang,
                        Text = candidate.Text,
                        Confidence = candidate.Confidence,
                        Score = score
                    };
                }

                var scoredCandidate = new TranscriptionResult
                {
                    Language = lang,
                    Text = candidate.Text,
                    Confidence = candidate.Confidence,
                    Score = score
                };

                if (lang == OCRLanguage.English)
                {
                    if (string.IsNullOrWhiteSpace(bestEnglish.Text) || scoredCandidate.Score > bestEnglish.Score)
                    {
                        bestEnglish = scoredCandidate;
                    }
                }
                else if (ContainsCjk(scoredCandidate.Text))
                {
                    if (string.IsNullOrWhiteSpace(bestCjk.Text) || scoredCandidate.Score > bestCjk.Score)
                    {
                        bestCjk = scoredCandidate;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(bestCjk.Text) && !string.IsNullOrWhiteSpace(bestEnglish.Text))
        {
            // When CJK evidence exists, avoid flipping to English too aggressively.
            if (bestEnglish.Score < bestCjk.Score + 1.8)
            {
                best = bestCjk;
            }
        }

        if (!string.IsNullOrWhiteSpace(best.Text))
        {
            _autoLanguageHint = best.Language;
            LastStatus = $"Auto selected {best.Language} (score {best.Score:F2}, conf {best.Confidence:F2}).";
            return best;
        }

        if (preferCjk)
        {
            // Only fallback to English when all CJK candidates fail.
            var englishModel = await EnsureModelAsync(OCRLanguage.English, ct);
            if (englishModel != null)
            {
                LastStatus = "Auto fallback transcribing (English)...";
                var english = await TranscribeWaveFileAsync(normalizedPath, englishModel, ct);
                if (!string.IsNullOrWhiteSpace(english.Text))
                {
                    var englishResult = new TranscriptionResult
                    {
                        Language = OCRLanguage.English,
                        Text = english.Text,
                        Confidence = english.Confidence,
                        Score = ScoreAutoCandidate(OCRLanguage.English, english.Text, english.Confidence)
                    };
                    _autoLanguageHint = OCRLanguage.English;
                    return englishResult;
                }
            }
        }

        LastStatus = "No installed Vosk model matched Auto mode (JA/ZH/KO/EN), or confidence too low.";
        return default;
    }

    private static double ScoreAutoCandidate(OCRLanguage lang, string text, double confidence)
    {
        if (string.IsNullOrWhiteSpace(text)) return double.MinValue;

        int han = 0;
        int kana = 0;
        int hangul = 0;
        int latin = 0;
        int digits = 0;
        int punct = 0;
        foreach (char ch in text)
        {
            if (IsKana(ch)) kana++;
            else if (IsHangul(ch)) hangul++;
            else if (IsHan(ch)) han++;
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) latin++;
            else if (char.IsDigit(ch)) digits++;
            else if (char.IsPunctuation(ch) || char.IsSymbol(ch)) punct++;
        }

        int scriptChars = han + kana + hangul + latin + digits;
        double nonScriptPenalty = punct > 0 ? punct * 0.1 : 0;
        double confidenceScore = Math.Clamp(confidence, 0, 1) * 2.4;
        double lengthBoost = Math.Min(text.Length, 28) * 0.035;
        double scriptScore = lang switch
        {
            OCRLanguage.TraditionalChinese or OCRLanguage.SimplifiedChinese =>
                (han * 3.0) - (kana * 2.0) - (hangul * 2.0) - (latin * 0.2),
            OCRLanguage.Japanese =>
                (kana * 3.0) + (han * 1.2) - (hangul * 2.0),
            OCRLanguage.Korean =>
                (hangul * 3.0) - (kana * 2.0) - (han * 0.5),
            OCRLanguage.English =>
                (latin * 2.6) + (digits * 0.7) - (han * 0.5) - (kana * 0.6) - (hangul * 0.6),
            _ => scriptChars * 0.05
        };

        return scriptScore + confidenceScore + lengthBoost - nonScriptPenalty;
    }

    private static bool IsKana(char ch)
    {
        return (ch >= '\u3040' && ch <= '\u309F') || (ch >= '\u30A0' && ch <= '\u30FF');
    }

    private static bool IsHangul(char ch)
    {
        return ch >= '\uAC00' && ch <= '\uD7AF';
    }

    private static bool IsHan(char ch)
    {
        return ch >= '\u4E00' && ch <= '\u9FFF';
    }

    private static bool ContainsCjk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char ch in text)
        {
            if (IsHan(ch) || IsKana(ch) || IsHangul(ch))
            {
                return true;
            }
        }
        return false;
    }

    private void EnsureLoopbackCaptureStarted()
    {
        if (_loopbackCapture != null) return;

        lock (_captureLock)
        {
            if (_loopbackCapture != null) return;

            _loopbackCapture = new WasapiLoopbackCapture();
            _loopbackFormat = _loopbackCapture.WaveFormat;
            _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
            _loopbackCapture.StartRecording();
            LastStatus = "Capturing system audio...";
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_captureLock)
        {
            _captureBuffer.Seek(0, SeekOrigin.End);
            _captureBuffer.Write(e.Buffer, 0, e.BytesRecorded);

            if (_loopbackFormat == null) return;

            long maxBytes = (long)_loopbackFormat.AverageBytesPerSecond * MaxBufferedSeconds;
            if (_captureBuffer.Length <= maxBytes) return;

            long keepFrom = _captureBuffer.Length - maxBytes;
            byte[] latest = _captureBuffer.GetBuffer().AsSpan((int)keepFrom, (int)maxBytes).ToArray();
            _captureBuffer.SetLength(0);
            _captureBuffer.Write(latest, 0, latest.Length);
        }
    }

    private bool TryWriteRecentAudioToWave(string outputPath, TimeSpan lookback)
    {
        byte[] snapshot;
        WaveFormat? format;

        lock (_captureLock)
        {
            format = _loopbackFormat;
            if (format == null || _captureBuffer.Length == 0) return false;

            long bytesToTake = (long)Math.Max(
                format.AverageBytesPerSecond,
                lookback.TotalSeconds * format.AverageBytesPerSecond);

            long available = _captureBuffer.Length;
            int take = (int)Math.Min(bytesToTake, available);
            int blockAlign = Math.Max(format.BlockAlign, 1);

            // Keep PCM frame boundaries aligned; misaligned chunks decode as noise.
            take -= take % blockAlign;
            if (take <= 0) return false;

            int start = (int)(available - take);
            start -= start % blockAlign;
            if (start < 0) start = 0;

            int end = start + take;
            if (end > available) end = (int)available;
            int finalTake = end - start;
            finalTake -= finalTake % blockAlign;
            if (finalTake <= 0) return false;

            snapshot = _captureBuffer.GetBuffer().AsSpan(start, finalTake).ToArray();
        }

        if (snapshot.Length == 0 || format == null) return false;

        using var writer = new WaveFileWriter(outputPath, format);
        writer.Write(snapshot, 0, snapshot.Length);
        writer.Flush();
        return true;
    }

    private async Task<Model?> EnsureModelAsync(OCRLanguage sourceLanguage, CancellationToken ct)
    {
        await _modelLock.WaitAsync(ct);
        try
        {
            var (tag, _) = ResolveModelSpec(sourceLanguage);
            if (_voskModel != null && _loadedModelTag == tag) return _voskModel;

            string modelPath = Path.Combine(_modelDirectory, tag);
            if (!Directory.Exists(modelPath) || !LooksLikeVoskModel(modelPath))
            {
                LastStatus = $"Vosk model ({tag}) not installed. Install it from Modules first.";
                return null;
            }

            _voskModel?.Dispose();
            _voskModel = new Model(modelPath);
            _loadedModelTag = tag;
            LastStatus = "Vosk model ready.";
            return _voskModel;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioSTT][Vosk] EnsureModelAsync failed: {ex.Message}");
            LastStatus = $"Vosk init failed: {ex.Message}";
            return null;
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private static (string Tag, string Url) ResolveModelSpec(OCRLanguage sourceLanguage)
    {
        return sourceLanguage switch
        {
            OCRLanguage.SimplifiedChinese => ("small-cn-0.22", VoskCnModelUrl),
            OCRLanguage.TraditionalChinese => ("small-cn-0.22", VoskCnModelUrl),
            OCRLanguage.Japanese => ("small-ja-0.22", VoskJaModelUrl),
            OCRLanguage.Korean => ("small-ko-0.22", VoskKoModelUrl),
            OCRLanguage.Auto => ("small-ja-0.22", VoskJaModelUrl),
            _ => ("small-en-us-0.15", VoskEnModelUrl)
        };
    }

    private async Task DownloadAndExtractModelAsync(string modelUrl, string destination, CancellationToken ct)
    {
        string zipPath = Path.Combine(_tempDirectory, $"vosk_{Guid.NewGuid():N}.zip");
        string extractRoot = Path.Combine(_tempDirectory, $"vosk_extract_{Guid.NewGuid():N}");
        try
        {
            LastStatus = "Connecting to Vosk model host...";
            using var response = await _httpClient.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long? totalBytes = response.Content.Headers.ContentLength;
            await using (var fs = File.Create(zipPath))
            await using (var net = await response.Content.ReadAsStreamAsync(ct))
            {
                byte[] buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = downloaded * 100d / totalBytes.Value;
                        LastStatus = $"Downloading Vosk model... {pct:F1}% ({downloaded / 1024d / 1024d:F1} MB)";
                    }
                    else
                    {
                        LastStatus = $"Downloading Vosk model... {downloaded / 1024d / 1024d:F1} MB";
                    }
                }
            }

            Directory.CreateDirectory(extractRoot);
            LastStatus = "Extracting Vosk model package...";
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            string? extractedModelRoot = Directory.EnumerateDirectories(extractRoot, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(LooksLikeVoskModel);
            if (string.IsNullOrWhiteSpace(extractedModelRoot))
            {
                extractedModelRoot = Directory.EnumerateDirectories(extractRoot, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(LooksLikeVoskModel);
            }
            if (string.IsNullOrWhiteSpace(extractedModelRoot))
            {
                return;
            }

            if (Directory.Exists(destination))
            {
                LastStatus = "Replacing old Vosk model files...";
                Directory.Delete(destination, true);
            }

            LastStatus = "Installing Vosk model files...";
            CopyDirectory(extractedModelRoot, destination);
            LastStatus = "Vosk model install complete. Loading model...";
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDirectory(extractRoot);
        }
    }

    private static bool LooksLikeVoskModel(string path)
    {
        return Directory.Exists(Path.Combine(path, "am"))
               && Directory.Exists(Path.Combine(path, "conf"));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void NormalizeForSpeech(string inputPath, string outputPath)
    {
        using var reader = new AudioFileReader(inputPath);
        var targetFormat = new WaveFormat(16000, 16, 1);
        using var resampler = new MediaFoundationResampler(reader, targetFormat)
        {
            ResamplerQuality = 60
        };
        WaveFileWriter.CreateWaveFile(outputPath, resampler);
    }

    private static bool IsLikelySilent(string wavePath, out double db)
    {
        db = -120;
        try
        {
            using var reader = new WaveFileReader(wavePath);
            if (reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
            {
                return false;
            }

            byte[] buffer = new byte[4096];
            long sumSq = 0;
            long samples = 0;

            while (true)
            {
                int read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                for (int i = 0; i + 1 < read; i += 2)
                {
                    short sample = BitConverter.ToInt16(buffer, i);
                    sumSq += (long)sample * sample;
                    samples++;
                }
            }

            if (samples == 0) return true;
            double rms = Math.Sqrt(sumSq / (double)samples) / short.MaxValue;
            db = rms <= 0.0000001 ? -120 : 20 * Math.Log10(rms);
            return db < -48;
        }
        catch
        {
            return false;
        }
    }

    private void StopWorker()
    {
        if (_workerCts == null) return;
        try { _workerCts.Cancel(); } catch { }
        try { _workerTask?.Wait(1000); } catch { }
        try { _workerCts.Dispose(); } catch { }
        _workerCts = null;
        _workerTask = null;
    }

    private static async Task<(string Text, double Confidence)> TranscribeWaveFileAsync(
        string wavePath,
        Model model,
        CancellationToken ct)
    {
        try
        {
            using var waveReader = new WaveFileReader(wavePath);
            if (waveReader.WaveFormat.SampleRate != 16000
                || waveReader.WaveFormat.BitsPerSample != 16
                || waveReader.WaveFormat.Channels != 1)
            {
                return (string.Empty, 0);
            }

            using var recognizer = new VoskRecognizer(model, 16000.0f);
            recognizer.SetWords(true);
            var sb = new StringBuilder();
            double confidenceSum = 0;
            int confidenceCount = 0;
            byte[] buffer = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                int read = await waveReader.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) break;
                if (!recognizer.AcceptWaveform(buffer, read)) continue;
                AppendSegmentText(sb, recognizer.Result(), "text", ref confidenceSum, ref confidenceCount);
            }
            AppendSegmentText(sb, recognizer.FinalResult(), "text", ref confidenceSum, ref confidenceCount);
            string text = sb.ToString().Trim();
            if (text.Length < MinTranscriptLength)
            {
                return (string.Empty, 0);
            }

            double confidence = confidenceCount > 0
                ? confidenceSum / confidenceCount
                : 0.42;
            return (text, confidence);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioSTT] TranscribeWaveFile failed: {ex.Message}");
            return (string.Empty, 0);
        }
    }

    private static void AppendSegmentText(StringBuilder sb, string json, string key, ref double confidenceSum, ref int confidenceCount)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(key, out var textElement)) return;
            var text = textElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(text);

            if (doc.RootElement.TryGetProperty("result", out var words) && words.ValueKind == JsonValueKind.Array)
            {
                foreach (var token in words.EnumerateArray())
                {
                    if (token.TryGetProperty("conf", out var confElement)
                        && confElement.ValueKind == JsonValueKind.Number
                        && confElement.TryGetDouble(out double conf))
                    {
                        confidenceSum += Math.Clamp(conf, 0, 1);
                        confidenceCount++;
                    }
                }
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    public void Dispose()
    {
        StopWorker();
        try { _loopbackCapture?.StopRecording(); } catch { }
        if (_loopbackCapture != null)
        {
            _loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
            _loopbackCapture.Dispose();
            _loopbackCapture = null;
        }

        lock (_captureLock)
        {
            _captureBuffer.Dispose();
        }

        _voskModel?.Dispose();
        _modelLock.Dispose();
        _httpClient.Dispose();
    }
}
