using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using ReactiveUI;
using CliWrap;
using CliWrap.Buffered;
using GimmeCapture.Models;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

public enum RecordingState { Idle, Recording, Paused }

public class RecordingService : ReactiveObject
{
    private readonly FFmpegDownloaderService _downloader;
    private readonly AppSettingsService? _settingsService;
    private Process? _ffmpegProcess;
    private RecordingState _state = RecordingState.Idle;
    private readonly List<string> _segments = new();
    private readonly List<string> _audioSegments = new();
    private string _outputFile = string.Empty;
    private string _targetFormat = "mp4";
    private Rect _region;
    private bool _includeCursor = true;
    private PixelPoint _screenOffset;
    private double _visualScaling = 1.0;
    private int _fps = 30;
    private bool _recordSystemAudio;
    private bool _isFinalizing;
    private double _finalizationProgress;
    private string _tempDir = string.Empty;
    private WasapiLoopbackCapture? _audioCapture;
    private WaveFileWriter? _audioWriter;

    public RecordingState State
    {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    public string? OutputFilePath => _outputFile;
    public string? LastRecordingPath => string.IsNullOrEmpty(_outputFile) ? null : _outputFile;
    public void ClearLastRecording() { _outputFile = string.Empty; }

    public FFmpegDownloaderService Downloader => _downloader;

    public bool IsFinalizing
    {
        get => _isFinalizing;
        private set => this.RaiseAndSetIfChanged(ref _isFinalizing, value);
    }

    public double FinalizationProgress
    {
        get => _finalizationProgress;
        private set => this.RaiseAndSetIfChanged(ref _finalizationProgress, value);
    }

    public string BaseTempDir => Path.Combine(_settingsService?.BaseDataDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "Temp", "Recordings");

    public RecordingService(FFmpegDownloaderService downloader, AppSettingsService? settingsService = null)
    {
        _downloader = downloader;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Start recording with specified target format for final output.
    /// Recording is done in MKV format internally for fast pause/resume.
    /// </summary>
    public async Task<bool> StartAsync(Rect region, string outputFile, string targetFormat = "mp4", bool includeCursor = true, PixelPoint screenOffset = default, double visualScaling = 1.0, int fps = 30, bool recordSystemAudio = false)
    {
        if (State != RecordingState.Idle) return false;
        if (!_downloader.IsFFmpegAvailable()) return false;

        _region = region;
        _outputFile = outputFile;
        _targetFormat = targetFormat.ToLowerInvariant();
        _includeCursor = includeCursor;
        _screenOffset = screenOffset;
        _visualScaling = visualScaling;
        _fps = fps;
        _recordSystemAudio = recordSystemAudio;
        _segments.Clear();
        _audioSegments.Clear();

        // Use a unique temp directory for THIS session to avoid conflicts with zombie processes
        var baseDataDir = _settingsService?.BaseDataDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        _tempDir = Path.Combine(baseDataDir, "Temp", $"Recordings_{Guid.NewGuid()}");

        // Ensure temp dir is clean (it's new so it should be, but just in case)
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to clear temp dir '{_tempDir}': {ex.Message}");
        }
        Directory.CreateDirectory(_tempDir);

        return await StartSegmentAsync();
    }

    private async Task<bool> StartSegmentAsync()
    {
        // Record segments in MKV format for instant pause/resume
        string segmentFile = Path.Combine(_tempDir, $"segment_{_segments.Count}.mkv");
        _segments.Add(segmentFile);

        // Calculate physical pixels for high-DPI
        // Keep coordinate conversion consistent with screen capture services:
        // scale logical region first, then apply physical screen offset.
        int x = (int)(_region.X * _visualScaling) + _screenOffset.X;
        int y = (int)(_region.Y * _visualScaling) + _screenOffset.Y;
        // Ensure even dimensions for video codecs
        int w = ((int)(_region.Width * _visualScaling) / 2) * 2;
        int h = ((int)(_region.Height * _visualScaling) / 2) * 2;

        if (_recordSystemAudio)
        {
            string audioSegment = Path.Combine(_tempDir, $"segment_{_segments.Count - 1}.wav");
            if (TryStartAudioCapture(audioSegment))
            {
                _audioSegments.Add(audioSegment);
            }
            else
            {
                Debug.WriteLine("Loopback capture unavailable, fallback to video-only.");
                _recordSystemAudio = false;
            }
        }

        var argsNoAudio = BuildSegmentArguments(x, y, w, h, segmentFile);
        var started = await StartFfmpegSegmentAsync(argsNoAudio);
        if (!started)
        {
            StopAudioCapture();
        }

        return started;
    }

    private bool TryStartAudioCapture(string audioSegmentPath)
    {
        try
        {
            StopAudioCapture();
            _audioCapture = new WasapiLoopbackCapture();
            _audioWriter = new WaveFileWriter(audioSegmentPath, _audioCapture.WaveFormat);
            _audioCapture.DataAvailable += (_, e) =>
            {
                TryWriteAudioBuffer(e.Buffer, e.BytesRecorded);
            };
            _audioCapture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start loopback capture: {ex.Message}");
            StopAudioCapture();
            return false;
        }
    }

    private void StopAudioCapture()
    {
        try
        {
            _audioCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stop loopback capture failed: {ex.Message}");
        }
        finally
        {
            DisposeAudioResources();
        }
    }

    private void DisposeAudioResources()
    {
        Exception? firstException = null;

        TryExecute("Dispose loopback capture", () => _audioCapture?.Dispose());
        TryExecute("Dispose wave writer", () => _audioWriter?.Dispose());

        _audioCapture = null;
        _audioWriter = null;

        if (firstException != null)
        {
            Debug.WriteLine($"Audio cleanup completed with errors: {firstException.Message}");
        }

        void TryExecute(string action, Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                firstException ??= ex;
                Debug.WriteLine($"{action} failed: {ex.Message}");
            }
        }
    }

    private void TryWriteAudioBuffer(byte[] buffer, int bytesRecorded)
    {
        try
        {
            _audioWriter?.Write(buffer, 0, bytesRecorded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Write loopback audio buffer failed: {ex.Message}");
        }
    }

    private string BuildSegmentArguments(int x, int y, int w, int h, string segmentFile)
    {
        string drawMouse = _includeCursor ? "1" : "0";
        string codec = _settingsService?.Settings.VideoCodec == VideoCodec.H265 ? "libx265" : "libx264";
        return $"-y -f gdigrab -draw_mouse {drawMouse} -framerate {_fps} -offset_x {x} -offset_y {y} -video_size {w}x{h} -i desktop " +
               $"-c:v {codec} -preset ultrafast -tune zerolatency -pix_fmt yuv420p \"{segmentFile}\"";
    }

    private async Task<bool> StartFfmpegSegmentAsync(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _downloader.FfmpegExecutablePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();

            // Read stderr in background to prevent buffer blocking.
            _ = PumpFfmpegStderrAsync(process);

            // Guard against immediate start failures (invalid device/args).
            await Task.Delay(400);
            if (process.HasExited)
            {
                Debug.WriteLine($"FFmpeg exited early ({process.ExitCode}).");
                TryDisposeProcess(process);
                return false;
            }

            _ffmpegProcess = process;
            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start FFmpeg: {ex.Message}");
            TryDisposeProcess(process);
            State = RecordingState.Idle;
            return false;
        }
    }

    public async Task PauseAsync()
    {
        if (State != RecordingState.Recording || _ffmpegProcess == null) return;

        await StopCurrentSegmentAsync();
        State = RecordingState.Paused;
    }

    private static async Task PumpFfmpegStderrAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line != null) Debug.WriteLine($"[FFmpeg] {line}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FFmpeg stderr pump stopped: {ex.Message}");
        }
    }

    public async Task ResumeAsync()
    {
        if (State != RecordingState.Paused) return;

        await StartSegmentAsync();
    }

    public async Task StopAsync()
    {
        if (State == RecordingState.Idle) return;

        if (State == RecordingState.Recording)
        {
            await StopCurrentSegmentAsync();
        }

        IsFinalizing = true;
        FinalizationProgress = 0;
        try
        {
            await FinalizeRecordingAsync();
        }
        finally
        {
            IsFinalizing = false;
            FinalizationProgress = 100;
            State = RecordingState.Idle;
        }
    }

    private async Task StopCurrentSegmentAsync()
    {
        var process = _ffmpegProcess;
        if (process == null) return;

        if (process.HasExited)
        {
            TryDisposeProcess(process);
            _ffmpegProcess = null;
            return;
        }

        try
        {
            TrySendQuitCommand(process);

            bool exitedInTime = await WaitForExitWithinAsync(process, timeoutMs: 1000);
            if (!exitedInTime)
            {
                Debug.WriteLine("FFmpeg did not exit gracefully, killing...");
                TryKillProcess(process);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping FFmpeg: {ex.Message}");
            TryKillProcess(process);
        }
        finally
        {
            TryDisposeProcess(process);
            if (ReferenceEquals(_ffmpegProcess, process))
            {
                _ffmpegProcess = null;
            }
            StopAudioCapture();
        }
    }

    private static void TrySendQuitCommand(Process process)
    {
        try
        {
            process.StandardInput.WriteLine("q");
            process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to send quit command to FFmpeg: {ex.Message}");
        }
    }

    private static async Task<bool> WaitForExitWithinAsync(Process process, int timeoutMs)
    {
        using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to kill FFmpeg process: {ex.Message}");
        }
    }

    private static void TryDisposeProcess(Process process)
    {
        try
        {
            process.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to dispose FFmpeg process: {ex.Message}");
        }
    }

    private async Task FinalizeRecordingAsync()
    {
        if (_segments.Count == 0 || string.IsNullOrEmpty(_outputFile)) return;

        // Wait a moment for file handles to be released
        await Task.Delay(100);

        var validSegments = GetValidVideoSegments();
        var validAudioSegments = GetValidAudioSegments();

        if (validSegments.Count == 0)
        {
            Debug.WriteLine("No valid segments to finalize!");
            return;
        }

        string mergedMkv = Path.Combine(_tempDir, "merged.mkv");

        try
        {
            mergedMkv = await MergeVideoSegmentsAsync(validSegments, mergedMkv);

            FinalizationProgress = 30;
            var mergedAudio = await MergeAudioSegmentsAsync(validAudioSegments);
            await FinalizeByTargetFormatAsync(mergedMkv, mergedAudio, cropFilter: null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error finalizing recording: {ex.Message}");
            System.Windows.Forms.MessageBox.Show($"Error saving recording: {ex.Message}", "Save Error");
        }
        finally
        {
            CleanupTempDirectory();
        }
    }

    private List<string> GetValidVideoSegments() =>
        _segments.Where(s => File.Exists(s) && new FileInfo(s).Length > 0).ToList();

    private List<string> GetValidAudioSegments() =>
        _audioSegments.Where(s => File.Exists(s) && new FileInfo(s).Length > 44).ToList();

    private async Task<string> MergeVideoSegmentsAsync(IReadOnlyList<string> validSegments, string mergedMkvPath)
    {
        if (validSegments.Count == 1)
        {
            return validSegments[0];
        }

        string listFile = Path.Combine(_tempDir, "list.txt");
        await WriteConcatListFileAsync(listFile, validSegments);

        string concatArgs = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{mergedMkvPath}\"";
        Debug.WriteLine($"[Finalize] Concat cmd: {concatArgs}");
        await RunFfmpegProcessAsync(concatArgs, "Concat");
        return mergedMkvPath;
    }

    private async Task<string?> MergeAudioSegmentsAsync(IReadOnlyList<string> validAudioSegments)
    {
        if (validAudioSegments.Count == 0) return null;
        if (validAudioSegments.Count == 1) return validAudioSegments[0];

        string audioListFile = Path.Combine(_tempDir, "audio_list.txt");
        await WriteConcatListFileAsync(audioListFile, validAudioSegments);

        string mergedAudio = Path.Combine(_tempDir, "merged_audio.wav");
        string concatAudioArgs = $"-y -f concat -safe 0 -i \"{audioListFile}\" -c:a pcm_s16le \"{mergedAudio}\"";
        Debug.WriteLine($"[Finalize] Concat audio cmd: {concatAudioArgs}");
        await RunFfmpegProcessAsync(concatAudioArgs, "Concat Audio");

        return File.Exists(mergedAudio) && new FileInfo(mergedAudio).Length > 44 ? mergedAudio : null;
    }

    private static async Task WriteConcatListFileAsync(string listPath, IEnumerable<string> items)
    {
        StringBuilder sb = new();
        foreach (var item in items)
        {
            sb.AppendLine($"file '{item.Replace("\\", "/")}'");
        }

        await File.WriteAllTextAsync(listPath, sb.ToString());
    }

    private async Task FinalizeByTargetFormatAsync(string mergedMkv, string? mergedAudio, string? cropFilter)
    {
        switch (_targetFormat)
        {
            case "mkv":
                await FinalizeAsMkvAsync(mergedMkv, mergedAudio, cropFilter);
                break;
            case "gif":
                await FinalizeAsGifAsync(mergedMkv, cropFilter);
                break;
            default:
                await FinalizeAsStandardVideoAsync(mergedMkv, mergedAudio, cropFilter);
                break;
        }
    }

    private async Task FinalizeAsMkvAsync(string mergedMkv, string? mergedAudio, string? cropFilter)
    {
        EnsureOutputExtension("mkv");

        if (string.IsNullOrWhiteSpace(cropFilter) && string.IsNullOrWhiteSpace(mergedAudio))
        {
            await TryMoveWithRetryAsync(mergedMkv, _outputFile);
        }
        else
        {
            var options = GetEncodingOptions();
            string mkvConvertArgs = string.IsNullOrWhiteSpace(mergedAudio)
                ? $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a aac -b:a 128k \"{_outputFile}\""
                : (string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -b:a 128k -shortest \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -vf \"{cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a aac -b:a 128k -shortest \"{_outputFile}\"");

            LogToFile($"MKV convert cmd: {mkvConvertArgs}");
            await RunFfmpegProcessAsync(mkvConvertArgs, "MKV Convert");
        }

        FinalizationProgress = 100;
    }

    private async Task FinalizeAsGifAsync(string mergedMkv, string? cropFilter)
    {
        var quality = _settingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
        int gifFps = quality switch
        {
            VideoQuality.High => Math.Min(30, _fps),
            VideoQuality.Low => Math.Min(10, _fps),
            _ => Math.Min(15, _fps)
        };

        string scale = quality switch
        {
            VideoQuality.High => "iw",
            VideoQuality.Low => "min(480,iw)",
            _ => "min(720,iw)"
        };

        string dither = quality switch
        {
            VideoQuality.High => "bayer:bayer_scale=2",
            VideoQuality.Low => "none",
            _ => "bayer:bayer_scale=5"
        };

        string paletteuse = quality == VideoQuality.High ? $"paletteuse=dither={dither}:new=1" : $"paletteuse=dither={dither}";
        string palettegen = quality == VideoQuality.High ? "palettegen=stats_mode=single" : "palettegen";
        string baseGifFilter = $"fps={gifFps},scale='{scale}':-1:flags=lanczos";

        string paletteFile = Path.Combine(_tempDir, "palette.png");
        string paletteArgs = string.IsNullOrWhiteSpace(cropFilter)
            ? $"-y -i \"{mergedMkv}\" -vf \"{baseGifFilter},{palettegen}\" \"{paletteFile}\""
            : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter},{baseGifFilter},{palettegen}\" \"{paletteFile}\"";

        await RunFfmpegProcessAsync(paletteArgs, "GIF Palette");

        FinalizationProgress = 60;

        string gifFlags = quality == VideoQuality.Low ? "-gifflags +transdiff" : "";
        string gifArgs = string.IsNullOrWhiteSpace(cropFilter)
            ? $"-y -i \"{mergedMkv}\" -i \"{paletteFile}\" {gifFlags} -lavfi \"{baseGifFilter} [x]; [x][1:v] {paletteuse}\" \"{_outputFile}\""
            : $"-y -i \"{mergedMkv}\" -i \"{paletteFile}\" {gifFlags} -lavfi \"{cropFilter},{baseGifFilter} [x]; [x][1:v] {paletteuse}\" \"{_outputFile}\"";

        await RunFfmpegProcessAsync(gifArgs, "GIF Encode");

        FinalizationProgress = 100;
    }

    private async Task FinalizeAsStandardVideoAsync(string mergedMkv, string? mergedAudio, string? cropFilter)
    {
        EnsureOutputExtension(_targetFormat);
        var options = GetEncodingOptions();

        string convertArgs;
        if (!string.IsNullOrWhiteSpace(mergedAudio))
        {
            convertArgs = _targetFormat switch
            {
                "webm" => string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -c:v libvpx-vp9 -crf {options.WebmCrf} -b:v 0 -cpu-used {options.WebmCpuUsed} -c:a libopus -shortest \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -vf \"{cropFilter}\" -c:v libvpx-vp9 -crf {options.WebmCrf} -b:v 0 -cpu-used {options.WebmCpuUsed} -c:a libopus -shortest \"{_outputFile}\"",
                "mov" => string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a aac -b:a 128k -shortest -f mov \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -vf \"{cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -c:a aac -b:a 128k -shortest -f mov \"{_outputFile}\"",
                _ => string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -c:a aac -b:a 128k -shortest -movflags +faststart \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -i \"{mergedAudio}\" -map 0:v:0 -map 1:a:0 -vf \"{cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -c:a aac -b:a 128k -shortest -movflags +faststart \"{_outputFile}\""
            };
        }
        else
        {
            convertArgs = _targetFormat switch
            {
                "webm" => string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -c:v libvpx-vp9 -crf {options.WebmCrf} -b:v 0 -cpu-used {options.WebmCpuUsed} \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v libvpx-vp9 -crf {options.WebmCrf} -b:v 0 -cpu-used {options.WebmCpuUsed} \"{_outputFile}\"",
                "mov" => string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -f mov \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -pix_fmt yuv420p -f mov \"{_outputFile}\"",
                _ => string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -movflags +faststart \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v {options.Codec} -preset {options.Preset} -crf {options.Crf} -movflags +faststart \"{_outputFile}\""
            };
        }

        Debug.WriteLine($"[Finalize] Convert cmd: {convertArgs}");
        await RunFfmpegProcessAsync(convertArgs, "Convert");
        Debug.WriteLine($"[Finalize] Output exists={File.Exists(_outputFile)}, size={(File.Exists(_outputFile) ? new FileInfo(_outputFile).Length : 0)}");
        FinalizationProgress = 100;
    }

    private (string Codec, string Crf, string Preset, string WebmCrf, string WebmCpuUsed) GetEncodingOptions()
    {
        bool isH265 = _settingsService?.Settings.VideoCodec == VideoCodec.H265;
        string codec = isH265 ? "libx265" : "libx264";
        var quality = _settingsService?.Settings.VideoQuality ?? VideoQuality.Medium;

        string crf = quality switch
        {
            VideoQuality.High => "18",
            VideoQuality.Low => isH265 ? "32" : "28",
            _ => isH265 ? "28" : "23"
        };

        string preset = quality switch
        {
            VideoQuality.High => "slower",
            VideoQuality.Low => "fast",
            _ => "medium"
        };

        string webmCpuUsed = quality switch
        {
            VideoQuality.High => "1",
            VideoQuality.Low => "4",
            _ => "2"
        };

        string webmCrf = quality switch
        {
            VideoQuality.High => "18",
            VideoQuality.Low => "35",
            _ => "25"
        };

        return (codec, crf, preset, webmCrf, webmCpuUsed);
    }

    private void EnsureOutputExtension(string expectedExtension)
    {
        string currentExt = Path.GetExtension(_outputFile).ToLowerInvariant().TrimStart('.');
        if (currentExt != expectedExtension)
        {
            _outputFile = Path.ChangeExtension(_outputFile, expectedExtension);
        }
    }

    private async Task TryMoveWithRetryAsync(string sourcePath, string destinationPath)
    {
        Exception? lastEx = null;
        for (int i = 0; i < 5; i++)
        {
            if (TryMoveWithFallback(sourcePath, destinationPath, out lastEx))
            {
                return;
            }

            await Task.Delay(500);
        }

        if (lastEx != null) throw lastEx;
    }

    private bool TryMoveWithFallback(string sourcePath, string destinationPath, out Exception? error)
    {
        try
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }

        if (TryMoveFile(sourcePath, destinationPath, out var moveError))
        {
            error = null;
            return true;
        }

        if (moveError is not IOException)
        {
            error = moveError;
            return false;
        }

        try
        {
            File.Copy(sourcePath, destinationPath, true);
            TryDeleteFile(sourcePath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static bool TryMoveFile(string sourcePath, string destinationPath, out Exception? error)
    {
        try
        {
            File.Move(sourcePath, destinationPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to delete file '{path}': {ex.Message}");
        }
    }

    private void CleanupTempDirectory()
    {
        try
        {
            if (!Directory.Exists(_tempDir)) return;
            if (_tempDir.Contains("Recordings_"))
            {
                Directory.Delete(_tempDir, true);
                return;
            }

            Directory.Delete(_tempDir, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to cleanup temp directory '{_tempDir}': {ex.Message}");
        }
    }

    private async Task RunFfmpegProcessAsync(string arguments, string label)
    {
        try
        {
            var result = await Cli.Wrap(_downloader.FfmpegExecutablePath)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            LogToFile($"{label}: ExitCode={result.ExitCode}");
            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
            {
                LogToFile($"{label} STDERR: {result.StandardError}");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"{label}: Failed to run FFmpeg via CliWrap. {ex.Message}");
            throw;
        }
    }

    private void LogToFile(string message)
    {
        try
        {
            var logPath = Path.Combine(_settingsService?.BaseDataDirectory ?? AppDomain.CurrentDomain.BaseDirectory, "recording_debug.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            Debug.WriteLine($"[RecordingService] {message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingService] Failed to write log file: {ex.Message}");
        }
    }
}
