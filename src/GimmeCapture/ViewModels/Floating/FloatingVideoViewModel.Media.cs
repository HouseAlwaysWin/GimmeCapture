using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using ReactiveUI.Reactive;
using System;
using System.Reactive;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using System.Diagnostics;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
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
            var target = PlayheadSeconds() + 5;
            // Only clamp to the end once the duration is known — at boot it can still be 0 (the callback from
            // PlayAsync fills it), and clamping against 0 would produce a negative target the seek gate drops.
            if (_totalDuration > TimeSpan.Zero && target >= _totalDuration.TotalSeconds)
            {
                target = _totalDuration.TotalSeconds - 0.1;
            }
            _seekTargetSeconds = target;

            // Just seek and update
            StartPlayback();
        });

        RewindCommand = ReactiveCommand.Create(() => 
        {
            var target = PlayheadSeconds() - 5;
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

        _bootMediaTask = BootMediaAsync();
    }

    private Task BootMediaAsync()
    {
        // Start playback immediately instead of blocking first frame behind a separate duration probe: PlayAsync
        // already opens+scans the file to decode, and now reports the duration it reads via onDurationKnown
        // (see ApplyProbedDuration below), so a large file is scanned once, not twice.
        if (!_isDisposed)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_isDisposed)
                {
                    StartPlayback();
                }
            });
        }

        return Task.CompletedTask;
    }

    // Fed by LibavVideoFramePlayer.PlayAsync on open; sets the slider's known length without a second file scan.
    // PlayAsync re-opens on every seek/segment/loop pass, so short-circuit once the (invariant) duration is known
    // to avoid re-posting to the UI thread on every pass.
    private void ApplyProbedDuration(double seconds)
    {
        if (_isDisposed || seconds <= 0 || Math.Abs(TotalDuration.TotalSeconds - seconds) <= 0.01)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed && seconds > 0 && Math.Abs(TotalDuration.TotalSeconds - seconds) > 0.01)
            {
                TotalDuration = TimeSpan.FromSeconds(seconds);
            }
        });
    }

    /// <summary>
    /// 在背景取消目前播放，不阻塞呼叫端。
    /// </summary>
    private void CancelPlaybackInBackground()
    {
        var oldCts = CancelPlaybackToken();
        if (oldCts != null)
        {
            try
            {
                oldCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        StopAudioPlayback();
    }

    private CancellationTokenSource? CancelPlaybackToken()
    {
        var oldCts = Interlocked.Exchange(ref _playCts, null);
        if (oldCts != null)
        {
            try
            {
                oldCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return oldCts;
    }


    // UI thread (every caller is, the debounced seek included).
    private void StartPlayback()
    {
        if (_isDisposed)
        {
            return;
        }

        // Read before the generation moves on, after which the decoder's last position no longer counts.
        double startSeconds = _seekTargetSeconds >= 0 ? _seekTargetSeconds : PlayheadSeconds();
        _seekTargetSeconds = -1;

        // Cancel old playback in background (never blocks)
        CancelPlaybackInBackground();
        var generation = Interlocked.Increment(ref _playbackGeneration);

        // Timeline edit: clamp the restart point to the first kept piece; otherwise the whole clip. The loop gets the
        // pieces as they are now (a change is otherwise handed over once the UI work that made it is done).
        var keptForStart = _keptForPlayback.Refresh();
        var effectiveEnd = TotalDuration;
        double effectiveStart = keptForStart.Length > 0 ? keptForStart[0].SourceStart : 0;

        bool wrapped = effectiveEnd > TimeSpan.Zero && startSeconds >= effectiveEnd.TotalSeconds;
        if (wrapped)
        {
            startSeconds = effectiveStart;
        }

        // The time text and seek bar follow once the first frame from here is decoded; only a wrap back to the start
        // moves the seek bar now (before the duration is known, a later position would be past its end).
        _currentTime = TimeSpan.FromSeconds(startSeconds);
        if (wrapped)
        {
            this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
        }

        _playCts = new CancellationTokenSource();
        _playbackTask = PlaybackLoopFixed(_playCts.Token, generation, startSeconds);
    }

    private async Task PlaybackLoopFixed(CancellationToken ct, int generation, double firstStartSeconds)
    {
        // Ensure only one loop runs at a time
        await _playSemaphore.WaitAsync(ct);
        
        try
        {
            // Where the next pass starts. It is kept here rather than in _currentTime, which only the UI thread
            // writes; a move the loop makes itself (next piece, loop restart) reaches the UI through MovePlayhead.
            double nextStartSeconds = firstStartSeconds;
            while (!ct.IsCancellationRequested && !_isDisposed)
            {
                _trimEndReached = false;

                bool playSingleFrame = !_isPlaybackActive;
                // Walk the KEPT pieces (one or many), skipping dropped ones. A single kept piece just
                // plays its [start,end] range (= a trim). Independent of IsTimelineMode, so a collapsed
                // panel still previews the edit. From the UI thread's copy: this runs on the pool, and
                // EditSegments is the UI thread's to change.
                VideoEditSegment[] kept = _keptForPlayback.Kept;
                bool multi = !playSingleFrame && kept.Length >= 1;

                double startSeconds = nextStartSeconds;
                double passEnd;
                int segIndex = -1;

                if (multi)
                {
                    segIndex = ResolveSegmentForPlayback(kept, ref startSeconds);
                    if (segIndex < 0)
                    {
                        // Past the last kept piece: behave like end of clip.
                        if (!IsLooping)
                        {
                            _isPlaybackActive = false;
                            StopAudioPlayback();
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(IsPlaying)));
                            break;
                        }

                        segIndex = 0;
                        startSeconds = kept[0].SourceStart;
                    }

                    if (startSeconds != nextStartSeconds)
                    {
                        MovePlayhead(startSeconds, generation); // snapped out of a cut gap into the next kept piece
                    }

                    passEnd = kept[segIndex].SourceEnd;
                }
                else
                {
                    // Not in timeline mode (or a single paused-seek frame): play the whole clip unbounded.
                    passEnd = double.MaxValue;
                }

                // Actively stop the decode at the segment/trim end (not just freeze the frame) when
                // playing a bounded range, so the next segment starts at once instead of decoding to EOF.
                bool stopAtPassEnd = playSingleFrame || multi;
                using var passCts = stopAtPassEnd ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;

                // Honor the per-piece speed during preview (on top of the global playback-speed control).
                double pieceSpeed = (multi && segIndex >= 0 && kept[segIndex].Speed > 0) ? kept[segIndex].Speed : 1.0;
                double effectivePlaybackSpeed = _playbackSpeed * pieceSpeed;

                // Keep looping control in this VM so audio/video restart together every cycle.
                bool loopPlayback = false;

                try
                {
                    UpdateAudioStateFromPlayback(startSeconds, kept);
                    await _nativeFramePlayer.PlayFramesAsync(
                        VideoPath,
                        _width,
                        _height,
                        startSeconds,
                        effectivePlaybackSpeed,
                        loopPlayback,
                        (bgra, seconds) =>
                        {
                            if (_isDisposed || generation != Volatile.Read(ref _playbackGeneration)) return;
                            if (seconds >= passEnd)
                            {
                                _trimEndReached = true;
                                passCts?.Cancel();
                                return;
                            }

                            // The decode thread only hands over the frame and its time; the UI refreshes pick them
                            // up. A paused seek decodes just this one frame, so it is shown even inside the throttle.
                            UpdateBitmap(bgra, generation, force: playSingleFrame);
                            _decodedPlayhead.Record(Math.Max(0, seconds), generation);
                            RequestCurrentTimeUiRefresh(force: playSingleFrame);

                            if (playSingleFrame)
                            {
                                passCts?.Cancel();
                            }
                        },
                        stopAtPassEnd ? passCts!.Token : ct,
                        onDurationKnown: ApplyProbedDuration).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (playSingleFrame || _trimEndReached)
                {
                    // expected for one-frame seek, trim end, or segment end
                }
                finally
                {
                    // The last frames of a pass can land inside the refresh throttle and never be shown: show the
                    // frame and time it stopped on (pause, end of the clip or of a piece).
                    if (generation == Volatile.Read(ref _playbackGeneration))
                    {
                        TryScheduleFrameUiRefresh(force: true);
                        RequestCurrentTimeUiRefresh(force: true);
                    }
                }

                if (ct.IsCancellationRequested)
                    break;

                if (!_isPlaybackActive)
                {
                    // Was just seeking a single frame while paused. Exit loop.
                    break;
                }

                // The next pass restarts the audio at its own start (UpdateAudioStateFromPlayback above).
                if (multi)
                {
                    // Advance to the next kept piece (skipping the dropped gap), or loop/stop at the end.
                    if (segIndex + 1 < kept.Length)
                    {
                        nextStartSeconds = kept[segIndex + 1].SourceStart;
                        MovePlayhead(nextStartSeconds, generation);
                        continue;
                    }

                    if (!IsLooping)
                    {
                        _isPlaybackActive = false;
                        StopAudioPlayback();
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(IsPlaying)));
                        break;
                    }

                    nextStartSeconds = kept[0].SourceStart;
                    MovePlayhead(nextStartSeconds, generation);
                    continue;
                }

                if (!IsLooping)
                {
                    _isPlaybackActive = false;
                    StopAudioPlayback();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(IsPlaying)));
                    break;
                }

                // Whole-clip loop (not in timeline mode): restart from the beginning.
                nextStartSeconds = 0;
                MovePlayhead(nextStartSeconds, generation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warning("FloatingVideo.Playback", ex);
        }
        finally
        {
            _playSemaphore.Release();
        }
    }

    /// <summary>
    /// Picks the kept segment to play for the given source time and snaps the time into it. A time in
    /// a cut gap (or before the first segment) snaps forward to the next segment's start. Returns the
    /// segment index, or -1 when the time is past the last kept segment.
    /// </summary>
    private static int ResolveSegmentForPlayback(VideoEditSegment[] kept, ref double sourceSeconds)
    {
        double s = sourceSeconds;
        for (int i = 0; i < kept.Length; i++)
        {
            VideoEditSegment seg = kept[i];
            if (s < seg.SourceEnd - 1e-3)
            {
                sourceSeconds = Math.Max(s, seg.SourceStart);
                return i;
            }
        }

        return -1;
    }

    // `kept` is the pieces the playback plays (a pass's own, or the latest the UI thread handed over).
    private void UpdateAudioStateFromPlayback(double fromSeconds, VideoEditSegment[] kept)
    {
        if (_isDisposed || !_isPlaybackActive || IsMuted)
        {
            StopAudioPlayback();
            return;
        }

        StartAudioPlayback(fromSeconds, kept);
    }

    // Returns at once: the audio player opens the file and the device on its own worker (Start replaces what played).
    private void StartAudioPlayback(double fromSeconds, VideoEditSegment[] kept)
    {
        if (_isDisposed || IsMuted || !_isPlaybackActive)
        {
            _audioPreview.Stop();
            return;
        }

        // Preview audio must honor the per-segment speed too (the timeline blocks' 1.5×/0.5×/2×),
        // not just the global playback-speed button — otherwise a sped-up segment's video runs fast
        // while its audio stays at the global rate. Mirrors the video's effectivePlaybackSpeed.
        _audioPreview.Start(VideoPath, Math.Max(0, fromSeconds), EffectiveAudioSpeed(fromSeconds, kept));
    }

    /// <summary>
    /// Effective preview audio speed = global playback-speed button × the speed of the kept segment
    /// under the playhead at <paramref name="atSeconds"/> (1× when not in a timeline segment). Matches the
    /// video's effectivePlaybackSpeed so audio and video stay in sync across per-segment speed changes.
    /// </summary>
    private double EffectiveAudioSpeed(double atSeconds, VideoEditSegment[] kept)
    {
        double pieceSpeed = 1.0;
        if (_isPlaybackActive && kept.Length >= 1)
        {
            double s = Math.Max(0, atSeconds);
            int idx = ResolveSegmentForPlayback(kept, ref s);
            if (idx >= 0 && kept[idx].Speed > 0)
            {
                pieceSpeed = kept[idx].Speed;
            }
        }

        return _playbackSpeed * pieceSpeed;
    }

    private void StopAudioPlayback() => _audioPreview.Stop();

    // Where the decoder has got to. CurrentTime trails it by up to one time refresh while a clip plays, so a seek,
    // speed change or resume measured from CurrentTime would jump back a frame or two. While the seek bar is being
    // dragged, the playhead is where the user is dragging it.
    private double PlayheadSeconds()
    {
        if (!_isDraggingSlider && _decodedPlayhead.TryPeek(Volatile.Read(ref _playbackGeneration), out double seconds))
        {
            return seconds;
        }

        return _currentTime.TotalSeconds;
    }

    // UI thread: moves the playhead and everything that shows it (time text, seek bar, redaction preview).
    private void ShowPlayheadAt(double seconds)
    {
        CurrentTime = TimeSpan.FromSeconds(Math.Max(0, seconds));
        this.RaisePropertyChanged(nameof(CurrentTimeSeconds));
    }

    // The loop moved the playhead itself (next kept piece, loop restart): it reaches the UI like a decoded frame's time.
    private void MovePlayhead(double seconds, int generation)
    {
        _decodedPlayhead.Record(seconds, generation);
        RequestCurrentTimeUiRefresh(force: true);
    }

    // UI thread: shows the time the decoder last reached, unless the user is dragging the seek bar.
    private void ApplyDecodedPlayhead()
    {
        if (_isDraggingSlider)
        {
            return;
        }

        if (_decodedPlayhead.TryTake(Volatile.Read(ref _playbackGeneration), out double seconds))
        {
            ShowPlayheadAt(seconds);
        }
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
            // Cleared before the time is read, so a time recorded from here on posts again instead of being missed.
            Interlocked.Exchange(ref _isTimeUiPostPending, 0);
            try
            {
                if (_isDisposed) return;
                ApplyDecodedPlayhead();
                Interlocked.Exchange(ref _lastTimeUiPostTimestampMs, Environment.TickCount64);
            }
            catch (Exception ex) { AppLog.Warning("FloatingVideo.PostTimeToUi", ex); }
        });
    }

    private void TryScheduleFrameUiRefresh(bool force = false)
    {
        if (_isDisposed) return;

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastFrameUiPostTimestampMs);
        if (!force && now - last < _uiFrameUpdateIntervalMs) return;
        if (Interlocked.CompareExchange(ref _isFrameUiPostPending, 1, 0) != 0) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Cleared before the frame is taken, so a frame published from here on posts again instead of being missed.
            Interlocked.Exchange(ref _isFrameUiPostPending, 0);
            try
            {
                if (ShowLatestFrame())
                {
                    RequestRedraw?.Invoke();
                    Interlocked.Exchange(ref _lastFrameUiPostTimestampMs, Environment.TickCount64);
                }
            }
            catch (Exception ex) { AppLog.Warning("FloatingVideo.PostFrameToUi", ex); }
        });
    }

    // UI thread: copies the newest decoded frame into the bitmap. False when there was no new frame to show.
    private bool ShowLatestFrame()
    {
        if (_isDisposed) return false;

        int generation = Volatile.Read(ref _playbackGeneration);
        lock (_videoBitmapLock)
        {
            var bitmap = _videoBitmap;
            if (bitmap == null || !_frameHandoff.HasNewFrame(generation)) return false;

            // The bitmap is locked first: the decoder waits for the copy below, never for the renderer.
            using var lockedBitmap = bitmap.Lock();
            int destinationCapacity = checked((int)(lockedBitmap.RowBytes * bitmap.PixelSize.Height));
            unsafe
            {
                var destination = new Span<byte>((void*)lockedBitmap.Address, destinationCapacity);
                return _frameHandoff.CopyLatestTo(destination, generation) > 0;
            }
        }
    }

    // Decode thread: copies the frame for the UI, which shows the newest one at its own pace. `force` shows it even
    // inside that pace's throttle (a paused seek's single frame).
    private void UpdateBitmap(ReadOnlySpan<byte> frame, int generation, bool force = false)
    {
        if (VideoBitmap == null || _isDisposed) return;
        if (generation != Volatile.Read(ref _playbackGeneration)) return;
        _frameHandoff.Publish(frame, generation);
        TryScheduleFrameUiRefresh(force);
    }
}
