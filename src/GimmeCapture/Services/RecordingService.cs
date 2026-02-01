using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using ReactiveUI;

namespace GimmeCapture.Services;

public enum RecordingState { Idle, Recording, Paused }

public class RecordingService : ReactiveObject
{
    private readonly FFmpegDownloaderService _downloader;
    private Process? _ffmpegProcess;
    private RecordingState _state = RecordingState.Idle;
    private readonly List<string> _segments = new();
    private string? _outputFile;
    private string _targetFormat = "mp4";
    private Rect _region;
    private string _tempDir;

    // Windows API for sending Ctrl+C
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    private const uint CTRL_C_EVENT = 0;

    public RecordingState State
    {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    // Expose the actual output file path (may be modified during finalization)
    public string? OutputFilePath => _outputFile;

    public RecordingService(FFmpegDownloaderService downloader)
    {
        _downloader = downloader;
        _tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Recordings");
    }

    /// <summary>
    /// Start recording with specified target format for final output.
    /// Recording is done in MKV format internally for fast pause/resume.
    /// </summary>
    public async Task<bool> StartAsync(Rect region, string outputFile, string targetFormat = "mp4")
    {
        if (State != RecordingState.Idle) return false;
        if (!_downloader.IsFFmpegAvailable()) return false;

        _region = region;
        _outputFile = outputFile;
        _targetFormat = targetFormat.ToLowerInvariant();
        _segments.Clear();

        // Ensure temp dir is clean
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

        int x = (int)_region.X;
        int y = (int)_region.Y;
        int w = ((int)_region.Width / 2) * 2;
        int h = ((int)_region.Height / 2) * 2;

        // Use MKV with zerolatency for instant pause response
        string args = $"-y -f gdigrab -framerate 30 -offset_x {x} -offset_y {y} -video_size {w}x{h} -i desktop " +
                      $"-c:v libx264 -preset ultrafast -tune zerolatency -pix_fmt yuv420p \"{segmentFile}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _downloader.FfmpegExecutablePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        _ffmpegProcess = new Process { StartInfo = startInfo };
        
        return await Task.Run(() =>
        {
            try
            {
                _ffmpegProcess.Start();
                // Read stderr in background to prevent buffer blocking
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_ffmpegProcess.HasExited)
                        {
                            var line = await _ffmpegProcess.StandardError.ReadLineAsync();
                            if (line != null) Debug.WriteLine($"[FFmpeg] {line}");
                        }
                    }
                    catch { /* ignore */ }
                });
                State = RecordingState.Recording;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start FFmpeg: {ex.Message}");
                State = RecordingState.Idle;
                return false;
            }
        });
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

        await FinalizeRecordingAsync();
        State = RecordingState.Idle;
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
                if (concatProcess != null)
                {
                    await concatProcess.WaitForExitAsync();
                }
            }

            // Step 2: Convert to target format if needed
            if (_targetFormat == "mkv")
            {
                // Ensure output file has correct extension
                string currentExt = Path.GetExtension(_outputFile).ToLowerInvariant().TrimStart('.');
                if (currentExt != "mkv")
                {
                    _outputFile = Path.ChangeExtension(_outputFile, "mkv");
                }
                
                // No conversion needed, just move
                if (File.Exists(_outputFile)) File.Delete(_outputFile);
                File.Move(mergedMkv, _outputFile);
            }
            else if (_targetFormat == "gif")
            {
                // Convert to GIF with palette for better quality
                string paletteFile = Path.Combine(_tempDir, "palette.png");
                
                // Generate palette
                string paletteArgs = $"-y -i \"{mergedMkv}\" -vf \"fps=15,scale=640:-1:flags=lanczos,palettegen\" \"{paletteFile}\"";
                var paletteInfo = new ProcessStartInfo
                {
                    FileName = _downloader.FfmpegExecutablePath,
                    Arguments = paletteArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var paletteProcess = Process.Start(paletteInfo);
                if (paletteProcess != null) await paletteProcess.WaitForExitAsync();

                // Create GIF using palette
                string gifArgs = $"-y -i \"{mergedMkv}\" -i \"{paletteFile}\" -lavfi \"fps=15,scale=640:-1:flags=lanczos[x];[x][1:v]paletteuse\" \"{_outputFile}\"";
                var gifInfo = new ProcessStartInfo
                {
                    FileName = _downloader.FfmpegExecutablePath,
                    Arguments = gifArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var gifProcess = Process.Start(gifInfo);
                if (gifProcess != null) await gifProcess.WaitForExitAsync();
            }
            else
            {
                // Ensure output file has correct extension
                string outputPath = _outputFile;
                string currentExt = Path.GetExtension(outputPath).ToLowerInvariant().TrimStart('.');
                if (currentExt != _targetFormat)
                {
                    outputPath = Path.ChangeExtension(outputPath, _targetFormat);
                    _outputFile = outputPath;
                }

                // Convert to MP4/MOV/WebM etc with proper settings
                string convertArgs = _targetFormat switch
                {
                    "webm" => $"-y -i \"{mergedMkv}\" -c:v libvpx-vp9 -crf 30 -b:v 0 -c:a libopus \"{_outputFile}\"",
                    "mov" => $"-y -i \"{mergedMkv}\" -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -f mov \"{_outputFile}\"",
                    _ => $"-y -i \"{mergedMkv}\" -c:v libx264 -preset fast -crf 23 -movflags +faststart \"{_outputFile}\""
                };

                var convertInfo = new ProcessStartInfo
                {
                    FileName = _downloader.FfmpegExecutablePath,
                    Arguments = convertArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var convertProcess = Process.Start(convertInfo);
                if (convertProcess != null)
                {
                    await convertProcess.WaitForExitAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error finalizing recording: {ex.Message}");
        }

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
