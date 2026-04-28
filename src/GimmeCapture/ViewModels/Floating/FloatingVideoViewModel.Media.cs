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
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;

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
            
            // Just seek and update 
            StartPlayback();
        });

        RewindCommand = ReactiveCommand.Create(() => 
        {
            var target = _currentTime.TotalSeconds - 5;
            if (target < 0) target = 0;
            _seekTargetSeconds = target;
            
            // Just seek and update
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

        _ = BootMediaAsync();
    }

    private async Task BootMediaAsync()
    {
        try
        {
            await DetectDurationAsync().ConfigureAwait(false);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(StartPlayback);
        }
    }

    private async Task DetectDurationAsync()
    {
        try
        {
            var seconds = await _nativeFramePlayer.ProbeDurationSecondsAsync(VideoPath).ConfigureAwait(false);
            if (seconds.HasValue && seconds.Value > 0)
            {
                TotalDuration = TimeSpan.FromSeconds(seconds.Value);
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
        var generation = Interlocked.Increment(ref _playbackGeneration);

        // 裁切模式：判斷是否超過裁切終點或影片結尾
        var effectiveEnd = IsTrimmingMode && TrimEndSeconds > 0 
            ? TimeSpan.FromSeconds(TrimEndSeconds) 
            : TotalDuration;
        var effectiveStart = IsTrimmingMode && TrimStartSeconds > 0
            ? TimeSpan.FromSeconds(TrimStartSeconds)
            : TimeSpan.Zero;

        if (_seekTargetSeconds >= 0)
        {
            _currentTime = TimeSpan.FromSeconds(_seekTargetSeconds);
            _seekTargetSeconds = -1;
        }

        if (_currentTime >= effectiveEnd && effectiveEnd > TimeSpan.Zero)
        {
            _currentTime = effectiveStart;
            this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
        }
        
        _playCts = new CancellationTokenSource();
        _playbackTask = PlaybackLoopFixed(_playCts.Token, generation);
    }

    private async Task PlaybackLoopFixed(CancellationToken ct, int generation)
    {
        // Ensure only one loop runs at a time
        await _playSemaphore.WaitAsync(ct);
        
        try
        {
            while (!ct.IsCancellationRequested && !_isDisposed)
            {
                // 每次迭代重新讀取裁切值，讓拉桿拖拽即時生效
                var trimActive = IsTrimmingMode && _totalDuration.TotalSeconds > 0;
                var trimStart = trimActive ? TrimStartSeconds : 0;
                var trimEnd = trimActive ? TrimEndSeconds : double.MaxValue;

                _trimEndReached = false;

                double startSeconds = _currentTime.TotalSeconds;

                // Keep looping control in this VM so audio/video restart together every cycle.
                bool loopPlayback = false;
                bool playSingleFrame = !_isPlaybackActive;
                using var singleFrameCts = playSingleFrame ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;

                try
                {
                    UpdateAudioStateFromPlayback();
                    await _nativeFramePlayer.PlayAsync(
                        VideoPath,
                        _width,
                        _height,
                        startSeconds,
                        _playbackSpeed,
                        loopPlayback,
                        (frameData, seconds) =>
                        {
                            if (_isDisposed || generation != Volatile.Read(ref _playbackGeneration)) return;
                            if (seconds >= trimEnd)
                            {
                                _trimEndReached = true;
                                singleFrameCts?.Cancel();
                                return;
                            }

                            UpdateBitmap(frameData, generation);
                            CurrentTime = TimeSpan.FromSeconds(Math.Max(0, seconds));
                            RequestCurrentTimeUiRefresh();

                            if (playSingleFrame)
                            {
                                singleFrameCts?.Cancel();
                            }
                        },
                        playSingleFrame ? singleFrameCts!.Token : ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (playSingleFrame || _trimEndReached)
                {
                    // expected for one-frame seek or trim end
                }

                if (ct.IsCancellationRequested)
                    break;
                    
                if (!_isPlaybackActive)
                {
                    // Was just seeking a single frame while paused. Exit loop.
                    break;
                }

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
                RequestCurrentTimeUiRefresh(force: true);
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
        StopAudioPlayback();
        if (_isDisposed || IsMuted || !_isPlaybackActive) return;

        try
        {
            var reader = new MediaFoundationReader(VideoPath);
            reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, _currentTime.TotalSeconds));
            _audioPlaybackStream = reader;
            _pinAudioPlayer = CreatePinAudioOutput(_audioPlaybackStream);

            _pinAudioPlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PinAudio] output: {ex.Message}");
            StopAudioPlayback();
        }
    }

    private static IWavePlayer CreatePinAudioOutput(WaveStream stream)
    {
        var wasapi = new WasapiOut();
        try
        {
            wasapi.Init(stream);
            return wasapi;
        }
        catch
        {
            wasapi.Dispose();
        }

        var waveOut = new WaveOutEvent();
        waveOut.Init(stream);
        return waveOut;
    }

    private void StopAudioPlayback()
    {
        try
        {
            _pinAudioDecodeCts?.Cancel();
            _pinAudioDecodeCts?.Dispose();
            _pinAudioDecodeCts = null;
            _pinAudioPlayer?.Stop();
            _pinAudioPlayer?.Dispose();
            _pinAudioPlayer = null;
            _audioPlaybackStream?.Dispose();
            _audioPlaybackStream = null;
        }
        catch { }
    }

    private void RequestCurrentTimeUiRefresh(bool force = false)
    {
        if (_isDisposed) return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastTimeUiPostTimestampMs);
        if (!force && now - last < _uiTimeUpdateIntervalMs) return;
        if (Interlocked.CompareExchange(ref _isTimeUiPostPending, 1, 0) != 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_isDisposed) return;
                this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
                Interlocked.Exchange(ref _lastTimeUiPostTimestampMs, Environment.TickCount64);
            }
            finally
            {
                Interlocked.Exchange(ref _isTimeUiPostPending, 0);
            }
        });
    }

    private void QueueLatestFrame(byte[] frameData, int generation)
    {
        lock (_latestFrameLock)
        {
            if (_latestFrameData == null || _latestFrameData.Length != frameData.Length)
            {
                _latestFrameData = new byte[frameData.Length];
            }

            Buffer.BlockCopy(frameData, 0, _latestFrameData, 0, frameData.Length);
            _latestFrameGeneration = generation;
        }
    }

    private void TryScheduleFrameUiRefresh()
    {
        if (_isDisposed) return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastFrameUiPostTimestampMs);
        if (now - last < _uiFrameUpdateIntervalMs) return;
        if (Interlocked.CompareExchange(ref _isFrameUiPostPending, 1, 0) != 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_isDisposed || VideoBitmap == null) return;

                byte[]? frameData;
                int generation;
                lock (_latestFrameLock)
                {
                    frameData = _latestFrameData;
                    generation = _latestFrameGeneration;
                }

                if (frameData == null) return;
                if (generation != Volatile.Read(ref _playbackGeneration)) return;

                using (var lockedBitmap = VideoBitmap.Lock())
                {
                    Marshal.Copy(frameData, 0, lockedBitmap.Address, frameData.Length);
                }

                this.RaisePropertyChanged(nameof(VideoBitmap));
                RequestRedraw?.Invoke();
                Interlocked.Exchange(ref _lastFrameUiPostTimestampMs, Environment.TickCount64);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _isFrameUiPostPending, 0);
            }
        });
    }

    internal void UpdateBitmap(byte[] frameData, int generation)
    {
        if (VideoBitmap == null || _isDisposed) return;
        if (generation != Volatile.Read(ref _playbackGeneration)) return;
        QueueLatestFrame(frameData, generation);
        TryScheduleFrameUiRefresh();
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
                    
                    if (!_vm._isDraggingSlider && _vm._isPlaybackActive)
                    {
                        var newTime = _vm.CurrentTime + TimeSpan.FromSeconds((1.0 / 30.0) * _vm.PlaybackSpeed);
                        if (newTime > _vm.TotalDuration) newTime = _vm.TotalDuration;
                        
                        // 裁切模式：超過結束時間就停止
                        if (newTime.TotalSeconds >= _trimEndSeconds)
                        {
                            _vm.CurrentTime = TimeSpan.FromSeconds(_trimEndSeconds);
                            _vm._trimEndReached = true;
                            _vm.RequestCurrentTimeUiRefresh(force: true);
                            throw new OperationCanceledException();
                        }
                        
                        _vm.CurrentTime = newTime;
                        _vm.RequestCurrentTimeUiRefresh();
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
