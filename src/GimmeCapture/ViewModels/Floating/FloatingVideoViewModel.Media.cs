using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
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

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            IsMuted = !IsMuted;
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

        StopAudioPlayback();
    }

    private void StartPlayback()
    {
        // Cancel old playback in background (never blocks)
        CancelPlaybackInBackground();

        // 裁切模式：判斷是否超過裁切終點或影片結尾
        var effectiveEnd = IsTrimmingMode && TrimEndSeconds > 0 
            ? TimeSpan.FromSeconds(TrimEndSeconds) 
            : TotalDuration;
        var effectiveStart = IsTrimmingMode && TrimStartSeconds > 0
            ? TimeSpan.FromSeconds(TrimStartSeconds)
            : TimeSpan.Zero;

        if (_currentTime >= effectiveEnd && effectiveEnd > TimeSpan.Zero)
        {
            _currentTime = effectiveStart;
            this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
        }
        
        _playCts = new CancellationTokenSource();
        _playbackTask = PlaybackLoopFixed(_playCts.Token);
        UpdateAudioStateFromPlayback();
    }

    private async Task PlaybackLoopFixed(CancellationToken ct)
    {
        // Ensure only one loop runs at a time
        await _playSemaphore.WaitAsync(ct);
        var generation = Interlocked.Increment(ref _playbackGeneration);
        
        try
        {
            while (!ct.IsCancellationRequested && !_isDisposed)
            {
                // 每次迭代重新讀取裁切值，讓拉桿拖拽即時生效
                var trimActive = IsTrimmingMode && _totalDuration.TotalSeconds > 0;
                var trimStart = trimActive ? TrimStartSeconds : 0;
                var trimEnd = trimActive ? TrimEndSeconds : double.MaxValue;

                _trimEndReached = false;

                var seekArg = "";
                if (_seekTargetSeconds >= 0)
                {
                    seekArg = $"-ss {_seekTargetSeconds:F3} ";
                    _currentTime = TimeSpan.FromSeconds(_seekTargetSeconds);
                    _seekTargetSeconds = -1;
                }
                else
                {
                    seekArg = $"-ss {_currentTime.TotalSeconds:F3} ";
                }

                using var pipe = new MemoryStream();
                var frameSize = _width * _height * 4;
                
                var filter = $"[0:v]setpts={1.0/_playbackSpeed}*PTS,fps=30,realtime[v]";

                var cmd = Cli.Wrap(_ffmpegPath)
                    .WithArguments($"{seekArg}-i \"{VideoPath}\" -filter_complex \"{filter}\" -map \"[v]\" -f image2pipe -vcodec rawvideo -pix_fmt bgra -s {_width}x{_height} -sws_flags fast_bilinear -loglevel quiet -")
                    .WithStandardOutputPipe(PipeTarget.ToStream(new FrameStreamWriter(this, frameSize, generation, trimEnd)));

                try
                {
                    await cmd.ExecuteAsync(ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested && _trimEndReached)
                {
                    // 裁切終點到達，這是預期行為
                }

                if (ct.IsCancellationRequested)
                    break;

                if (!IsLooping) 
                {
                    _isPlaybackActive = false;
                    StopAudioPlayback();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(IsPlaying)));
                    break;
                }
                
                // 循環播放：重新讀取最新的裁切起點
                var loopStart = IsTrimmingMode ? TrimStartSeconds : 0;
                _currentTime = TimeSpan.FromSeconds(loopStart);
                UpdateAudioStateFromPlayback();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(CurrentTimeSeconds)));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Playback Error: {ex.Message}");
        }
        finally
        {
            _playSemaphore.Release();
        }
    }

    private void UpdateAudioStateFromPlayback()
    {
        if (_isDisposed || !_isPlaybackActive || IsMuted)
        {
            StopAudioPlayback();
            return;
        }

        StartAudioPlayback();
    }

    private void StartAudioPlayback()
    {
        try
        {
            StopAudioPlayback();

            var ffplayPath = _ffmpegPath.Contains("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                ? _ffmpegPath.Replace("ffmpeg.exe", "ffplay.exe", StringComparison.OrdinalIgnoreCase)
                : _ffmpegPath;

            if (!File.Exists(ffplayPath))
            {
                System.Diagnostics.Debug.WriteLine($"Audio playback skipped: ffplay not found ({ffplayPath})");
                return;
            }

            var seekSeconds = Math.Max(0, _currentTime.TotalSeconds).ToString("F3", CultureInfo.InvariantCulture);
            var tempo = Math.Clamp(PlaybackSpeed, 0.5, 2.0).ToString("F2", CultureInfo.InvariantCulture);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffplayPath,
                Arguments = $"-nodisp -autoexit -loglevel quiet -ss {seekSeconds} -af \"atempo={tempo}\" -i \"{VideoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _audioPlayProcess = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audio Playback Error: {ex.Message}");
            StopAudioPlayback();
        }
    }

    private void StopAudioPlayback()
    {
        try
        {
            if (_audioPlayProcess != null)
            {
                if (!_audioPlayProcess.HasExited)
                {
                    _audioPlayProcess.Kill();
                }

                _audioPlayProcess.Dispose();
                _audioPlayProcess = null;
            }
        }
        catch { }
    }

    internal void UpdateBitmap(byte[] frameData, int generation)
    {
        if (VideoBitmap == null || _isDisposed) return;
        if (generation != Volatile.Read(ref _playbackGeneration)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() => 
        {
            if (generation != Volatile.Read(ref _playbackGeneration) || _isDisposed) return;
            
            try 
            {
                using (var lockedBitmap = VideoBitmap.Lock())
                {
                    Marshal.Copy(frameData, 0, lockedBitmap.Address, frameData.Length);
                }
                this.RaisePropertyChanged(nameof(VideoBitmap));
                RequestRedraw?.Invoke();
            }
            catch { }
        });
    }

    private class FrameStreamWriter : Stream
    {
        private readonly FloatingVideoViewModel _vm;
        private readonly int _frameSize;
        private readonly int _generation;
        private readonly double _trimEndSeconds;
        private byte[] _buffer;
        private int _totalRead = 0;

        public FrameStreamWriter(FloatingVideoViewModel vm, int frameSize, int generation, double trimEndSeconds = double.MaxValue)
        {
            _vm = vm;
            _frameSize = frameSize;
            _generation = generation;
            _trimEndSeconds = trimEndSeconds;
            _buffer = new byte[frameSize];
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_vm._isDisposed || _generation != Volatile.Read(ref _vm._playbackGeneration))
            {
                // Stop FFmpeg if this is a stale stream
                throw new OperationCanceledException();
            }

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
                    _vm.UpdateBitmap(_buffer, _generation);
                    _totalRead = 0;
                    
                    if (!_vm._isDraggingSlider)
                    {
                        var newTime = _vm.CurrentTime + TimeSpan.FromSeconds((1.0 / 30.0) * _vm.PlaybackSpeed);
                        if (newTime > _vm.TotalDuration) newTime = _vm.TotalDuration;
                        
                        // 裁切模式：超過結束時間就停止
                        if (newTime.TotalSeconds >= _trimEndSeconds)
                        {
                            _vm.CurrentTime = TimeSpan.FromSeconds(_trimEndSeconds);
                            _vm._trimEndReached = true;
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                _vm.RaisePropertyChanged(nameof(_vm.CurrentTimeSeconds)));
                            throw new OperationCanceledException();
                        }
                        
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
