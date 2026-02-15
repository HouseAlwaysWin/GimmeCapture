using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CliWrap;
using CliWrap.Buffered;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    private void InitializeMediaCommands()
    {
        TogglePlaybackCommand = ReactiveCommand.Create(() => 
        {
            _isPlaybackActive = !_isPlaybackActive;
            if (_isPlaybackActive) 
            {
                StartPlayback();
            }
            else
            {
                // Fire-and-forget cancel: don't block UI thread
                CancelPlaybackInBackground();
            }
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

    /// <summary>
    /// 在背景取消目前播放，不阻塞呼叫端。
    /// </summary>
    private void CancelPlaybackInBackground()
    {
        var oldCts = _playCts;
        _playCts = null;
        if (oldCts != null)
        {
            Task.Run(() => { try { oldCts.Cancel(); oldCts.Dispose(); } catch { } });
        }
    }

    private void StartPlayback()
    {
        // Cancel old playback in background (never blocks)
        CancelPlaybackInBackground();

        // If we are at the end, restart from zero
        if (_currentTime >= TotalDuration && TotalDuration > TimeSpan.Zero)
        {
            _currentTime = TimeSpan.Zero;
            this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
        }
        
        _playCts = new CancellationTokenSource();
        _playbackTask = PlaybackLoopFixed(_playCts.Token);
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
