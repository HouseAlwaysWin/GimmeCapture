using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ReactiveUI;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

public enum RecordingState { Idle, Recording, Paused }

public partial class RecordingService : ReactiveObject
{
    private readonly FFmpegDownloaderService _downloader;
    private readonly AppSettingsService? _settingsService;
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
    private int _startInProgress;
    private string _lastSelectedVideoEncoderName = string.Empty;

    public RecordingState State
    {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    public string? OutputFilePath => _outputFile;
    public string? LastRecordingPath => string.IsNullOrEmpty(_outputFile) ? null : _outputFile;
    public string LastStartError { get; private set; } = string.Empty;
    public string LastStartWarning { get; private set; } = string.Empty;
    public string LastSelectedVideoEncoderName => _lastSelectedVideoEncoderName;
    public void ClearLastRecording() { _outputFile = string.Empty; }
    
    public long GetCurrentRecordingSizeBytes()
    {
        if (State == RecordingState.Idle) return 0;
        long totalSize = 0;
        try
        {
            foreach (var file in _segments)
            {
                if (File.Exists(file)) totalSize += new FileInfo(file).Length;
            }
            foreach (var file in _audioSegments)
            {
                if (File.Exists(file)) totalSize += new FileInfo(file).Length;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to retrieve recording segment size: {ex.Message}");
        }

        // Apply estimation multiplier because intermediate MKV H.264 segments
        // are much more compressed than the finalized GIF format.
        if (_targetFormat == "gif")
        {
            var quality = _settingsService?.Settings.VideoQuality ?? VideoQuality.Medium;
            double multiplier = quality switch
            {
                VideoQuality.High => 4.5,
                VideoQuality.Medium => 3.0,
                VideoQuality.Low => 1.8,
                _ => 3.0
            };
            totalSize = (long)(totalSize * multiplier);
        }

        return totalSize;
    }

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
        if (Interlocked.Exchange(ref _startInProgress, 1) == 1) return false;
        try
        {
        LastStartError = string.Empty;
        LastStartWarning = string.Empty;
        _lastSelectedVideoEncoderName = string.Empty;
        if (State != RecordingState.Idle) return false;
        if (!FFmpegRuntime.IsInitialized)
        {
            FFmpegRuntime.TryInitialize(out var initError);
            if (!FFmpegRuntime.IsInitialized)
            {
                LastStartError = initError ?? "FFmpeg native runtime not initialized.";
                return false;
            }
        }

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
        finally
        {
            Interlocked.Exchange(ref _startInProgress, 0);
        }
    }

    private async Task<bool> StartSegmentAsync()
    {
        // Record segments in MKV format for pause/resume workflow.
        string segmentFile = Path.Combine(_tempDir, $"segment_{_segments.Count}.mkv");
        _segments.Add(segmentFile);

        // Calculate physical pixels for high-DPI
        // Keep coordinate conversion consistent with screen capture services:
        // scale logical region first, then apply physical screen offset.
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

        var started = await StartFfmpegSegmentAsync(segmentFile);
        if (!started)
        {
            StopAudioCapture();
        }

        return started;
    }

    public async Task PauseAsync()
    {
        if (State != RecordingState.Recording) return;

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

}
