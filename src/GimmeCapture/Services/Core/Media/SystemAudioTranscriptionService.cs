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
    private string _latestTranscript = string.Empty;

    public SystemAudioTranscriptionService(string baseDataDirectory)
    {
        _tempDirectory = Path.Combine(baseDataDirectory, "Temp", "AudioTranscription");
        _modelDirectory = Path.Combine(baseDataDirectory, "AIResources", "asr", "vosk");
        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_modelDirectory);
        Vosk.Vosk.SetLogLevel(-1);
    }

    public Task<string> CaptureAndTranscribeAsync(TimeSpan duration, OCRLanguage sourceLanguage, CancellationToken ct = default)
    {
        try
        {
            EnsureLoopbackCaptureStarted();
            EnsureWorkerStarted(sourceLanguage, duration);
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

    private void EnsureWorkerStarted(OCRLanguage sourceLanguage, TimeSpan lookback)
    {
        bool needRestart = _workerTask == null
                           || _workerTask.IsCompleted
                           || _workerLanguage != sourceLanguage;
        if (!needRestart) return;

        StopWorker();
        _workerLanguage = sourceLanguage;
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

                    var model = await EnsureModelAsync(_workerLanguage, ct);
                    if (model == null)
                    {
                        LastStatus = "Vosk model unavailable.";
                        await Task.Delay(500, ct);
                        continue;
                    }

                    LastStatus = "Transcribing...";
                    string text = await TranscribeWaveFileAsync(normalizedPath, model, ct);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _latestTranscript = text;
                        LastStatus = "Transcription updated.";
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
            var (tag, url) = ResolveModelSpec(sourceLanguage);
            if (_voskModel != null && _loadedModelTag == tag) return _voskModel;

            string modelPath = Path.Combine(_modelDirectory, tag);
            if (!Directory.Exists(modelPath) || !LooksLikeVoskModel(modelPath))
            {
                LastStatus = $"Downloading Vosk model ({tag})...";
                await DownloadAndExtractModelAsync(url, modelPath, ct);
            }

            if (!Directory.Exists(modelPath) || !LooksLikeVoskModel(modelPath)) return null;

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
            OCRLanguage.Auto => ("small-cn-0.22", VoskCnModelUrl),
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

    private static async Task<string> TranscribeWaveFileAsync(
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
                return string.Empty;
            }

            using var recognizer = new VoskRecognizer(model, 16000.0f);
            var sb = new StringBuilder();
            byte[] buffer = new byte[4096];
            while (!ct.IsCancellationRequested)
            {
                int read = await waveReader.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) break;
                if (!recognizer.AcceptWaveform(buffer, read)) continue;
                AppendSegmentText(sb, recognizer.Result(), "text");
            }
            AppendSegmentText(sb, recognizer.FinalResult(), "text");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioSTT] TranscribeWaveFile failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static void AppendSegmentText(StringBuilder sb, string json, string key)
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
