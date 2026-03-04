using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using ReactiveUI;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media;

public enum RecordingState { Idle, Recording, Paused }

public class RecordingService : ReactiveObject
{
    private readonly FFmpegDownloaderService _downloader;
    private readonly AppSettingsService? _settingsService;
    private Process? _ffmpegProcess;
    private RecordingState _state = RecordingState.Idle;
    private readonly List<string> _segments = new();
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? HandlerRoutine, bool Add);

    delegate bool ConsoleCtrlDelegate(uint CtrlType);

    private const uint CTRL_C_EVENT = 0;

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

        // Use a unique temp directory for THIS session to avoid conflicts with zombie processes
        var baseDataDir = _settingsService?.BaseDataDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        _tempDir = Path.Combine(baseDataDir, "Temp", $"Recordings_{Guid.NewGuid()}");

        // Ensure temp dir is clean (it's new so it should be, but just in case)
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { /* ignore */ }
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

        var started = false;
        if (_recordSystemAudio)
        {
            var audioCandidates = await BuildAudioInputCandidatesAsync();
            foreach (var audioInput in audioCandidates)
            {
                var argsWithAudio = BuildSegmentArguments(x, y, w, h, segmentFile, audioInput);
                started = await StartFfmpegSegmentAsync(argsWithAudio);
                if (started)
                {
                    Debug.WriteLine($"Audio capture enabled with input: {audioInput.Trim()}");
                    break;
                }
            }
        }

        if (!started)
        {
            if (_recordSystemAudio)
            {
                Debug.WriteLine("Audio capture unavailable, fallback to video-only for this segment.");
                _recordSystemAudio = false;
            }

            var argsNoAudio = BuildSegmentArguments(x, y, w, h, segmentFile, null);
            started = await StartFfmpegSegmentAsync(argsNoAudio);
        }

        return started;
    }

    private async Task<List<string>> BuildAudioInputCandidatesAsync()
    {
        var candidates = new List<string>
        {
            "-thread_queue_size 1024 -f wasapi -i default "
        };

        var stereoMixName = await TryFindStereoMixDeviceNameAsync();
        if (!string.IsNullOrWhiteSpace(stereoMixName))
        {
            var escaped = stereoMixName.Replace("\"", "\\\"");
            candidates.Add($"-thread_queue_size 1024 -f dshow -i audio=\"{escaped}\" ");
        }

        candidates.Add("-thread_queue_size 1024 -f dshow -i audio=\"virtual-audio-capturer\" ");
        return candidates;
    }

    private async Task<string?> TryFindStereoMixDeviceNameAsync()
    {
        try
        {
            var listInfo = new ProcessStartInfo
            {
                FileName = _downloader.FfmpegExecutablePath,
                Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };

            using var process = new Process { StartInfo = listInfo };
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (string.IsNullOrWhiteSpace(stderr)) return null;

            foreach (var line in stderr.Split('\n'))
            {
                if (line.IndexOf("stereo mix", StringComparison.OrdinalIgnoreCase) < 0) continue;

                var match = Regex.Match(line, "\"(?<name>[^\"]+)\"");
                if (match.Success)
                {
                    return match.Groups["name"].Value;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to discover audio devices: {ex.Message}");
        }

        return null;
    }

    private string BuildSegmentArguments(int x, int y, int w, int h, string segmentFile, string? audioInputArgs)
    {
        string drawMouse = _includeCursor ? "1" : "0";
        string codec = _settingsService?.Settings.VideoCodec == VideoCodec.H265 ? "libx265" : "libx264";
        string audioInput = audioInputArgs ?? string.Empty;
        bool includeAudio = !string.IsNullOrWhiteSpace(audioInputArgs);
        string audioCodec = includeAudio ? "-c:a aac -b:a 160k -ac 2 " : string.Empty;
        return $"-y -f gdigrab -draw_mouse {drawMouse} -framerate {_fps} -offset_x {x} -offset_y {y} -video_size {w}x{h} -i desktop " +
               $"{audioInput}-c:v {codec} -preset ultrafast -tune zerolatency -pix_fmt yuv420p {audioCodec}\"{segmentFile}\"";
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
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line != null) Debug.WriteLine($"[FFmpeg] {line}");
                    }
                }
                catch { /* ignore */ }
            });

            // Guard against immediate start failures (invalid device/args).
            await Task.Delay(400);
            if (process.HasExited)
            {
                Debug.WriteLine($"FFmpeg exited early ({process.ExitCode}).");
                process.Dispose();
                return false;
            }

            _ffmpegProcess = process;
            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start FFmpeg: {ex.Message}");
            try { process.Dispose(); } catch { }
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
        if (_ffmpegProcess == null) return;
        
        if (_ffmpegProcess.HasExited)
        {
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
            return;
        }

        try
        {
            // Try sending 'q' via stdin first
            try
            {
                _ffmpegProcess.StandardInput.WriteLine("q");
                _ffmpegProcess.StandardInput.Flush();
            }
            catch { /* stdin might be closed */ }

            // With zerolatency + MKV, exit should be very fast (< 0.5s)
            var cts = new System.Threading.CancellationTokenSource(1000);
            try
            {
                await _ffmpegProcess.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("FFmpeg did not exit gracefully, killing...");
                try { _ffmpegProcess.Kill(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping FFmpeg: {ex.Message}");
            try { _ffmpegProcess.Kill(); } catch { }
        }
        finally
        {
            try { _ffmpegProcess?.Dispose(); } catch { }
            _ffmpegProcess = null;
        }
    }

    private async Task FinalizeRecordingAsync()
    {
        if (_segments.Count == 0 || string.IsNullOrEmpty(_outputFile)) return;

        // Wait a moment for file handles to be released
        await Task.Delay(100);

        // Filter out segments that don't exist or are empty
        var validSegments = _segments.Where(s => File.Exists(s) && new FileInfo(s).Length > 0).ToList();
        
        if (validSegments.Count == 0)
        {
            Debug.WriteLine("No valid segments to finalize!");
            return;
        }

        string mergedMkv = Path.Combine(_tempDir, "merged.mkv");

        try
        {
            // Step 1: Merge all segments into a single MKV
            if (validSegments.Count == 1)
            {
                // Just use the single segment directly
                mergedMkv = validSegments[0];
            }
            else
            {
                // Concatenate segments
                string listFile = Path.Combine(_tempDir, "list.txt");
                StringBuilder sb = new();
                foreach (var segment in validSegments)
                {
                    sb.AppendLine($"file '{segment.Replace("\\", "/")}'");
                }
                await File.WriteAllTextAsync(listFile, sb.ToString());

                string concatArgs = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{mergedMkv}\"";

                var concatInfo = new ProcessStartInfo
                {
                    FileName = _downloader.FfmpegExecutablePath,
                    Arguments = concatArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var concatProcess = Process.Start(concatInfo);
                if (concatProcess != null) await concatProcess.WaitForExitAsync();
            }

            FinalizationProgress = 30;
            string? cropFilter = null;

            // Step 2: Convert/Move to target format
            if (_targetFormat == "mkv")
            {
                // Ensure output file has correct extension
                string currentExt = Path.GetExtension(_outputFile).ToLowerInvariant().TrimStart('.');
                if (currentExt != "mkv")
                {
                    _outputFile = Path.ChangeExtension(_outputFile, "mkv");
                }
                
                if (string.IsNullOrWhiteSpace(cropFilter))
                {
                    // Robust Move Strategy: Retry loops with Copy+Delete fallback
                    bool moveSuccess = false;
                    Exception? lastEx = null;

                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            if (File.Exists(_outputFile)) File.Delete(_outputFile);

                            // Try Move first
                            try
                            {
                                File.Move(mergedMkv, _outputFile);
                                moveSuccess = true;
                            }
                            catch (IOException)
                            {
                                // If Move fails (e.g. cross-volume or locked), try Copy+Delete
                                File.Copy(mergedMkv, _outputFile, true);
                                try { File.Delete(mergedMkv); } catch { /* best effort delete */ }
                                moveSuccess = true;
                            }

                            if (moveSuccess) break;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            await Task.Delay(500); // Wait before retry
                        }
                    }

                    if (!moveSuccess && lastEx != null) throw lastEx;
                }
                else
                {
                    string codec = _settingsService?.Settings.VideoCodec == VideoCodec.H265 ? "libx265" : "libx264";
                    string crf = _settingsService?.Settings.VideoCodec == VideoCodec.H265 ? "24" : "20";
                    string mkvConvertArgs = $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v {codec} -preset fast -crf {crf} -pix_fmt yuv420p -c:a aac -b:a 160k \"{_outputFile}\"";
                    var mkvInfo = new ProcessStartInfo { FileName = _downloader.FfmpegExecutablePath, Arguments = mkvConvertArgs, UseShellExecute = false, CreateNoWindow = true };
                    using var p = Process.Start(mkvInfo);
                    if (p != null) await p.WaitForExitAsync();
                }

                FinalizationProgress = 100;
            }
            else if (_targetFormat == "gif")
            {
                // ... (GIF logic omitted for brevity, assumes standard ffmpeg conversion)
                // Existing GIF logic
                string paletteFile = Path.Combine(_tempDir, "palette.png");
                string paletteArgs = string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -vf \"fps={_fps},palettegen\" \"{paletteFile}\""
                    : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter},fps={_fps},palettegen\" \"{paletteFile}\"";
                var paletteInfo = new ProcessStartInfo { FileName = _downloader.FfmpegExecutablePath, Arguments = paletteArgs, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(paletteInfo)) if (p != null) await p.WaitForExitAsync();

                FinalizationProgress = 60;

                string gifArgs = string.IsNullOrWhiteSpace(cropFilter)
                    ? $"-y -i \"{mergedMkv}\" -i \"{paletteFile}\" -lavfi \"fps={_fps} [x]; [x][1:v] paletteuse\" \"{_outputFile}\""
                    : $"-y -i \"{mergedMkv}\" -i \"{paletteFile}\" -lavfi \"{cropFilter},fps={_fps} [x]; [x][1:v] paletteuse\" \"{_outputFile}\"";
                var gifInfo = new ProcessStartInfo { FileName = _downloader.FfmpegExecutablePath, Arguments = gifArgs, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(gifInfo)) if (p != null) await p.WaitForExitAsync();
            }
            else
            {
                // ... (Other formats logic)
                string outputPath = _outputFile;
                string currentExt = Path.GetExtension(outputPath).ToLowerInvariant().TrimStart('.');
                if (currentExt != _targetFormat)
                {
                    outputPath = Path.ChangeExtension(outputPath, _targetFormat);
                    _outputFile = outputPath;
                }

                string codec = _settingsService?.Settings.VideoCodec == VideoCodec.H265 ? "libx265" : "libx264";
                string crf = _settingsService?.Settings.VideoCodec == VideoCodec.H265 ? "24" : "20"; // H265 is more efficient, can use higher CRF

                string convertArgs = _targetFormat switch
                {
                    "webm" => string.IsNullOrWhiteSpace(cropFilter)
                        ? $"-y -i \"{mergedMkv}\" -c:v libvpx-vp9 -crf 25 -b:v 0 -c:a libopus \"{_outputFile}\""
                        : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v libvpx-vp9 -crf 25 -b:v 0 -c:a libopus \"{_outputFile}\"",
                    "mov" => string.IsNullOrWhiteSpace(cropFilter)
                        ? $"-y -i \"{mergedMkv}\" -c:v {codec} -preset fast -crf {crf} -pix_fmt yuv420p -c:a aac -b:a 160k -f mov \"{_outputFile}\""
                        : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v {codec} -preset fast -crf {crf} -pix_fmt yuv420p -c:a aac -b:a 160k -f mov \"{_outputFile}\"",
                    _ => string.IsNullOrWhiteSpace(cropFilter)
                        ? $"-y -i \"{mergedMkv}\" -c:v {codec} -preset fast -crf {crf} -c:a aac -b:a 160k -movflags +faststart \"{_outputFile}\""
                        : $"-y -i \"{mergedMkv}\" -vf \"{cropFilter}\" -c:v {codec} -preset fast -crf {crf} -c:a aac -b:a 160k -movflags +faststart \"{_outputFile}\""
                };

                var convertInfo = new ProcessStartInfo { FileName = _downloader.FfmpegExecutablePath, Arguments = convertArgs, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(convertInfo)) if (p != null) await p.WaitForExitAsync();
                
                FinalizationProgress = 100;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error finalizing recording: {ex.Message}");
            System.Windows.Forms.MessageBox.Show($"Error saving recording: {ex.Message}", "Save Error");
        }
        
        // Cleanup: Only clean if it was a temp dir we created
        try
        {
            if (Directory.Exists(_tempDir) && _tempDir.Contains("Recordings_"))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { /* best effort */ }

        // Cleanup temp directory
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { /* ignore */ }
    }

}
