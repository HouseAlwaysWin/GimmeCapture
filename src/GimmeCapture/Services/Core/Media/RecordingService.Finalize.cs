using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
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

            Debug.WriteLine($"[Finalize] MKV convert cmd: {mkvConvertArgs}");
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

            Debug.WriteLine($"[Finalize] {label}: ExitCode={result.ExitCode}");
            if (result.ExitCode != 0)
            {
                throw new Exception($"{label} FFmpeg failed with exit code {result.ExitCode}. STDERR: {result.StandardError}");
            }
        }
        catch (Exception ex)
        {
            LogToFile($"{label}: FFmpeg error: {ex.Message}");
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
