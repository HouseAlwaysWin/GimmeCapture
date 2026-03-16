using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
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

            var startupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Read stderr in background to prevent buffer blocking and detect startup.
            _ = PumpFfmpegStderrAsync(process, startupTcs);

            // Wait for FFmpeg to signal "started" or timeout.
            // With reduced FFmpeg log level there may be no startup line,
            // so fallback to process liveness after timeout.
            var startTask = startupTcs.Task;
            var timeoutTask = Task.Delay(1200);

            var completedTask = await Task.WhenAny(startTask, timeoutTask);
            bool started = completedTask == startTask
                ? await startTask
                : !process.HasExited;

            if (!started || process.HasExited)
            {
                Debug.WriteLine($"FFmpeg failed to start properly (Exit={process.HasExited}, Code={ (process.HasExited ? process.ExitCode : "N/A") }).");
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

    private static async Task PumpFfmpegStderrAsync(Process process, TaskCompletionSource<bool>? startupTcs = null)
    {
        try
        {
            bool signaled = false;
            while (!process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line == null) break;

                if (!signaled && startupTcs != null)
                {
                    startupTcs.TrySetResult(true);
                    signaled = true;
                }

                // Keep runtime noise low during recording; only log warnings/errors.
                bool isImportant = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("warning", StringComparison.OrdinalIgnoreCase);
                if (isImportant)
                {
                    Debug.WriteLine($"[FFmpeg] {line}");
                }
            }

            if (startupTcs != null && !signaled)
            {
                startupTcs.TrySetResult(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FFmpeg stderr pump stopped: {ex.Message}");
            startupTcs?.TrySetResult(false);
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
}
