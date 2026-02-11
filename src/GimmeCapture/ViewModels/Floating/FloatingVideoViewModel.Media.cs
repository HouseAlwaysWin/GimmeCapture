using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using GimmeCapture.Models;
using System.Linq; // For Enumerable/List if needed

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    private WriteableBitmap? _videoBitmap;
    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        set => this.RaiseAndSetIfChanged(ref _videoBitmap, value);
    }

    public string VideoPath { get; }
    private readonly string _ffmpegPath;
    public string FFmpegPath => _ffmpegPath;
    public VideoCodec VideoCodec => _appSettingsService?.Settings.VideoCodec ?? VideoCodec.H264;
    private CancellationTokenSource? _playCts;
    private Task? _playbackTask;
    private readonly SemaphoreSlim _playSemaphore = new(1, 1);
    private readonly int _width;
    private readonly int _height;

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    private double _exportProgress;
    public double ExportProgress
    {
        get => _exportProgress;
        set => this.RaiseAndSetIfChanged(ref _exportProgress, value);
    }

    public bool IsPlaying => _isPlaybackActive;
    private bool _isPlaybackActive = true;

    private bool _isLooping = true;
    public bool IsLooping
    {
        get => _isLooping;
        set => this.RaiseAndSetIfChanged(ref _isLooping, value);
    }

    private TimeSpan _totalDuration = TimeSpan.Zero;
    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set 
        {
            this.RaiseAndSetIfChanged(ref _totalDuration, value);
            this.RaisePropertyChanged(nameof(FormattedTime));
        }
    }

    private TimeSpan _currentTime = TimeSpan.Zero;
    public TimeSpan CurrentTime
    {
        get => _currentTime;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTime, value);
            this.RaisePropertyChanged(nameof(FormattedTime));
        }
    }

    public string FormattedTime => $"{_currentTime:mm\\:ss} / {_totalDuration:mm\\:ss}";

    private double _seekTargetSeconds = -1;
    private double _playbackSpeed = 1.0;

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set 
        {
            if (Math.Abs(_playbackSpeed - value) < 0.01) return;
            
            _playbackSpeed = value;
            this.RaisePropertyChanged(nameof(PlaybackSpeed));
            this.RaisePropertyChanged(nameof(PlaybackSpeedText));
            
            // Speed change entails seeking from current point to avoid restart
            if (_isPlaybackActive)
            {
                _seekTargetSeconds = _currentTime.TotalSeconds;
                StartPlayback();
            }
        }
    }

    public string PlaybackSpeedText => $"{_playbackSpeed:F1}x";

    private bool _isDraggingSlider;
    private CancellationTokenSource? _seekDebounceCts;

    public double CurrentTimeSeconds
    {
        get => _currentTime.TotalSeconds;
        set
        {
            if (Math.Abs(_currentTime.TotalSeconds - value) > 0.01)
            {
                // Mark as user-dragging to suppress playback loop updates
                _isDraggingSlider = true;
                _currentTime = TimeSpan.FromSeconds(value);
                this.RaisePropertyChanged(nameof(FormattedTime));
                
                // Debounce the actual seek — only fire after user stops dragging for 300ms
                _seekDebounceCts?.Cancel();
                _seekDebounceCts = new CancellationTokenSource();
                var token = _seekDebounceCts.Token;
                _ = Task.Delay(300, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        _isDraggingSlider = false;
                        _seekTargetSeconds = value;
                        if (!_isPlaybackActive)
                        {
                            _isPlaybackActive = true;
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                this.RaisePropertyChanged(nameof(IsPlaying)));
                        }
                        StartPlayback();
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    public ReactiveCommand<Unit, Unit> TogglePlaybackCommand { get; private set; }
    public ReactiveCommand<Unit, Unit> FastForwardCommand { get; private set; }
    public ReactiveCommand<Unit, Unit> RewindCommand { get; private set; }
    public ReactiveCommand<Unit, Unit> ToggleLoopCommand { get; private set; }
    public ReactiveCommand<Unit, Unit> CycleSpeedCommand { get; private set; }

    private void InitializeMediaCommands()
    {
        TogglePlaybackCommand = ReactiveCommand.Create(() => 
        {
            _isPlaybackActive = !_isPlaybackActive;
            if (_isPlaybackActive) StartPlayback();
            else _playCts?.Cancel();
            this.RaisePropertyChanged(nameof(IsPlaying));
        });

        FastForwardCommand = ReactiveCommand.Create(() => 
        {
            var target = _currentTime.TotalSeconds + 5;
            if (target >= _totalDuration.TotalSeconds) target = _totalDuration.TotalSeconds - 0.1;
            _seekTargetSeconds = target;
            
            // Restart if paused to reflect seek immediately
            if (!_isPlaybackActive)
            {
                _isPlaybackActive = true;
                this.RaisePropertyChanged(nameof(IsPlaying));
            }
            StartPlayback();
        });

        RewindCommand = ReactiveCommand.Create(() => 
        {
            var target = _currentTime.TotalSeconds - 5;
            if (target < 0) target = 0;
            _seekTargetSeconds = target;
            
            if (!_isPlaybackActive)
            {
                _isPlaybackActive = true;
                this.RaisePropertyChanged(nameof(IsPlaying));
            }
            StartPlayback();
        });

        CycleSpeedCommand = ReactiveCommand.Create(() => 
        {
            // Just change the property, the setter handles the restart/seek logic
            PlaybackSpeed = PlaybackSpeed switch
            {
                0.5 => 1.0,
                1.0 => 1.5,
                1.5 => 2.0,
                2.0 => 0.5,
                _ => 1.0
            };
        });

        ToggleLoopCommand = ReactiveCommand.Create(() => 
        {
            IsLooping = !IsLooping;
        });
        
        // Initialize bitmap
        VideoBitmap = new WriteableBitmap(
            new PixelSize(_width, _height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        StartPlayback();
        _ = DetectDurationAsync();
    }

    private async Task DetectDurationAsync()
    {
        try
        {
            var ffprobePath = _ffmpegPath.Replace("ffmpeg.exe", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = "ffprobe.exe";

            var result = await Cli.Wrap(ffprobePath)
                .WithArguments($"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{VideoPath}\"")
                .ExecuteBufferedAsync();

            if (double.TryParse(result.StandardOutput.Trim(), out double seconds))
            {
                TotalDuration = TimeSpan.FromSeconds(seconds);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DetectDuration Error: {ex.Message}");
        }
    }

    private void StartPlayback()
    {
        _ = StartPlaybackInternal();
    }

    private async Task StartPlaybackInternal()
    {
        await _playSemaphore.WaitAsync();
        try
        {
            if (_playCts != null)
            {
                _playCts.Cancel();
                try 
                {
                    if (_playbackTask != null) await _playbackTask;
                } 
                catch { }
                finally 
                {
                    _playCts.Dispose();
                    _playCts = null;
                }
            }
            
            // If we are at the end, restart from zero
            if (_currentTime >= TotalDuration)
            {
                _currentTime = TimeSpan.Zero;
                this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
            }
            
            _playCts = new CancellationTokenSource();
            _playbackTask = PlaybackLoopFixed(_playCts.Token);
        }
        finally
        {
            _playSemaphore.Release();
        }
    }

    private async Task PlaybackLoopFixed(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var seekArg = "";
                if (_seekTargetSeconds >= 0)
                {
                    seekArg = $"-ss {_seekTargetSeconds:F3} ";
                    _currentTime = TimeSpan.FromSeconds(_seekTargetSeconds);
                    _seekTargetSeconds = -1;
                }
                else
                {
                    // Only reset to zero on a fresh start (not a speed-change restart)
                    seekArg = $"-ss {_currentTime.TotalSeconds:F3} ";
                }

                using var pipe = new MemoryStream();
                var frameSize = _width * _height * 4;
                
                // Speed filters + Realtime throttling
                var filter = $"[0:v]setpts={1.0/_playbackSpeed}*PTS,fps=30,realtime[v]";

                var cmd = Cli.Wrap(_ffmpegPath)
                    .WithArguments($"{seekArg}-i \"{VideoPath}\" -filter_complex \"{filter}\" -map \"[v]\" -f image2pipe -vcodec rawvideo -pix_fmt bgra -s {_width}x{_height} -sws_flags lanczos -loglevel quiet -")
                    .WithStandardOutputPipe(PipeTarget.ToStream(new FrameStreamWriter(this, frameSize)));

                await cmd.ExecuteAsync(ct);

                if (!IsLooping) 
                {
                    _isPlaybackActive = false;
                    this.RaisePropertyChanged(nameof(IsPlaying));
                    break;
                }
                
                // Reset for next loop
                _currentTime = TimeSpan.Zero;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(CurrentTimeSeconds)));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Playback Error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    internal void UpdateBitmap(byte[] frameData)
    {
        if (VideoBitmap == null) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
        {
            try 
            {
                using (var lockedBitmap = VideoBitmap.Lock())
                {
                    Marshal.Copy(frameData, 0, lockedBitmap.Address, frameData.Length);
                }
                this.RaisePropertyChanged(nameof(VideoBitmap));
                RequestRedraw?.Invoke();
            }
            catch { /* Handle potential disposal during update */ }
        });
    }

    public System.Action? RequestRedraw { get; set; }

    // Helper class to handle fixed-size frame writes
    private class FrameStreamWriter : Stream
    {
        private readonly FloatingVideoViewModel _vm;
        private readonly int _frameSize;
        private byte[] _buffer;
        private int _totalRead = 0;

        public FrameStreamWriter(FloatingVideoViewModel vm, int frameSize)
        {
            _vm = vm;
            _frameSize = frameSize;
            _buffer = new byte[frameSize];
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int remaining = count;
            int currentOffset = offset;

            while (remaining > 0)
            {
                int toCopy = Math.Min(remaining, _frameSize - _totalRead);
                Array.Copy(buffer, currentOffset, _buffer, _totalRead, toCopy);
                
                _totalRead += toCopy;
                currentOffset += toCopy;
                remaining -= toCopy;

                if (_totalRead == _frameSize)
                {
                    _vm.UpdateBitmap(_buffer);
                    _totalRead = 0;
                    
                    // Increment time based on output frames (30fps) scaled by speed
                    // Each output frame represents (1/30 * Speed) seconds of the source video
                    // Skip time update while user is dragging the slider
                    if (!_vm._isDraggingSlider)
                    {
                        var newTime = _vm.CurrentTime + TimeSpan.FromSeconds((1.0 / 30.0) * _vm.PlaybackSpeed);
                        if (newTime > _vm.TotalDuration) newTime = _vm.TotalDuration;
                        _vm.CurrentTime = newTime;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            _vm.RaisePropertyChanged(nameof(_vm.CurrentTimeSeconds)));
                    }
                }
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
    }
}
