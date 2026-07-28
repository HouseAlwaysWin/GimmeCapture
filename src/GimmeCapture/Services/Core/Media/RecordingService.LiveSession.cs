using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
#if WINDOWS
using GimmeCapture.Services.Platforms.Windows;
#endif

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    /// <summary>
    /// Hard cap on awaiting a WGC session's <c>StopAsync</c> so stop/pin can never hang the UI thread if the
    /// frame pool wedged (the dual-monitor "no frames" repro). On timeout we abandon the await and let
    /// Dispose tear the session down best-effort. See docs/WGC_HANDOFF.md "Fix A".
    /// </summary>
    private const int WgcStopTimeoutMs = 6000;

    /// <summary>
    /// The codec this recording will actually use.
    ///
    /// AV1 is only offered in the UI on machines with a usable hardware encoder, but the setting is persisted, so
    /// a config copied from such a machine (or a GPU/driver change) can still ask for AV1 here. Resolving it in
    /// one place means the encoder ladder never has to quietly emit H.265 under an "AV1" label, and the reason is
    /// in the log. Software AV1 is deliberately not a fallback — it cannot keep up with realtime capture.
    /// </summary>
    private VideoCodec ResolveRecordingCodec()
    {
        var codec = _settingsService?.Settings.VideoCodec ?? VideoCodec.H264;
        if (codec != VideoCodec.Av1 || LibavRecordingEncoder.HasUsableHardwareAv1Encoder())
        {
            return codec;
        }

        AppLog.Information("Recording.Av1Unavailable: no usable hardware AV1 encoder; recording H.265 instead.");
        return VideoCodec.H265;
    }

    private LibavGdigrabMkvSession? _nativeRecorder;
    private LibavWgcMkvSession? _nativeWgcRecorder;
    private LibavWgcCompositeMkvSession? _nativeWgcCompositeRecorder;

    /// <summary>Software-encoder CRF override (1-51; 0 = default 23) from settings, applied to every session.</summary>
    private int CustomVideoCrf => _settingsService?.Settings.CustomVideoCrf ?? 0;

    /// <summary>Hardware-encoder bitrate override in bits/sec (0 = auto clamp); settings store it as kbps.</summary>
    private long CustomVideoBitrateBps =>
        (_settingsService?.Settings.CustomVideoBitrateKbps ?? 0) > 0
            ? _settingsService!.Settings.CustomVideoBitrateKbps * 1000L
            : 0L;

    // Set once WGC is observed to bring up but deliver no frames (the dual-monitor "no frames" repro). After
    // that we skip WGC for the rest of the process and go straight to gdigrab, so the user stops paying the
    // ~1.5 s first-frame timeout on every recording. Reset only by restarting the app.
    private static bool _wgcNoFramesThisSession;

    private bool WgcAvailable =>
        !_wgcNoFramesThisSession
        && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
#if WINDOWS
        && WgcWindowCaptureSource.IsSupported;
#else
        ;
#endif

    private static void MarkWgcUnusableThisSession()
    {
        if (_wgcNoFramesThisSession)
        {
            return;
        }

        _wgcNoFramesThisSession = true;
        AppLog.Information("Wgc.Disabled (session): WGC produced no frames; skipping WGC and using gdigrab region capture for the rest of this run.");
    }

    private async Task<bool> StartFfmpegSegmentAsync(string segmentFile)
    {
        if (!FFmpegRuntime.IsInitialized)
        {
            Debug.WriteLine("[RecordingService] FFmpeg native runtime not initialized.");
            LastStartError = "FFmpeg native runtime not initialized.";
            return false;
        }

        // Whether to *attempt* WGC at all. Once WGC has been seen to deliver no frames this run, WgcAvailable is
        // false and we skip straight to the gdigrab fallbacks (no per-recording first-frame timeout, no warning).
        bool tryWgc = WgcAvailable;

        // Multiple windows: composite → one tiled video; separate → one file per window.
        if (_windowHandles.Count >= 2)
        {
            if (_multiWindowMode == MultiWindowMode.Separate && _tracks.Count > 0)
            {
                if (tryWgc)
                {
                    if (await StartSeparateSegmentsAsync().ConfigureAwait(false))
                    {
                        return true;
                    }

                    // WGC produced no frames (e.g. dual-monitor repro) — warn once; subsequent recordings skip WGC.
                    Debug.WriteLine($"[RecordingService] Separate WGC failed; per-window region capture. {LastStartError}");
                    LastStartWarning = "Window capture unavailable — recording each window's screen region instead.";
                }

                // Keep the per-window separate-files behaviour by capturing each window's screen rectangle.
                if (await StartSeparateGdigrabSegmentsAsync().ConfigureAwait(false))
                {
                    return true;
                }

                // Even per-window region capture failed: drop separate-files mode and record one region that will
                // finalize (clearing _tracks routes stop to the single-file finalize path).
                Debug.WriteLine($"[RecordingService] Per-window region fallback failed; single region. {LastStartError}");
                _tracks.Clear();
                LastStartWarning = "Multi-window capture unavailable — recording the screen region instead.";
                return await StartGdigrabSegmentAsync(segmentFile).ConfigureAwait(false);
            }

            if (tryWgc)
            {
                if (await StartCompositeSegmentAsync(segmentFile).ConfigureAwait(false))
                {
                    return true;
                }

                Debug.WriteLine($"[RecordingService] Composite WGC capture failed; region. {LastStartError}");
                LastStartWarning = "Multi-window capture unavailable — recording the screen region instead.";
            }

            return await StartGdigrabSegmentAsync(segmentFile).ConfigureAwait(false);
        }

        // A single picked window records via Windows Graphics Capture so the output follows the window. If
        // WGC can't start (no support / window gone / no frames this run), fall back to the gdigrab region.
        if (_windowHandle != IntPtr.Zero && tryWgc)
        {
            if (await StartWgcSegmentAsync(segmentFile).ConfigureAwait(false))
            {
                return true;
            }

            Debug.WriteLine($"[RecordingService] WGC window capture failed; falling back to region. {LastStartError}");
            LastStartWarning = "Window capture unavailable — recording the window's current screen region instead.";
        }

        return await StartGdigrabSegmentAsync(segmentFile).ConfigureAwait(false);
    }

    private async Task<bool> StartCompositeSegmentAsync(string segmentFile)
    {
        try
        {
            _nativeWgcCompositeRecorder?.Dispose();
            _nativeWgcCompositeRecorder = new LibavWgcCompositeMkvSession
            {
                PreferHardwareEncoder = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly,
                VideoCrf = CustomVideoCrf,
                VideoBitrate = CustomVideoBitrateBps
            };

            var codec = ResolveRecordingCodec();

            var ok = await _nativeWgcCompositeRecorder.StartAsync(segmentFile, _windowHandles, _fps, _includeCursor, codec)
                .ConfigureAwait(false);
            if (!ok)
            {
                if (_nativeWgcCompositeRecorder.TimedOutWaitingForFrame)
                {
                    MarkWgcUnusableThisSession();
                }

                LastStartError = _nativeWgcCompositeRecorder.LastErrorMessage ?? "Composite WGC session reported failure.";
                _nativeWgcCompositeRecorder.Dispose();
                _nativeWgcCompositeRecorder = null;
                return false;
            }

            LastStartWarning = _nativeWgcCompositeRecorder.LastWarningMessage ?? string.Empty;
            _lastSelectedVideoEncoderName = _nativeWgcCompositeRecorder.SelectedEncoderName ?? string.Empty;
            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            // The first-frame/bring-up timeout surfaces here as a thrown task (the gate re-throws the faulted
            // firstFrame), so the no-frames cache must also be checked on this path — not just the !ok branch.
            if (_nativeWgcCompositeRecorder?.TimedOutWaitingForFrame == true)
            {
                MarkWgcUnusableThisSession();
            }

            LastStartError = ex.Message;
            Debug.WriteLine($"[RecordingService] Composite recorder start failed: {ex.Message}");
            _nativeWgcCompositeRecorder?.Dispose();
            _nativeWgcCompositeRecorder = null;
            return false;
        }
    }

    private async Task<bool> StartWgcSegmentAsync(string segmentFile)
    {
        try
        {
            _nativeWgcRecorder?.Dispose();
            _nativeWgcRecorder = new LibavWgcMkvSession
            {
                PreferHardwareEncoder = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly,
                VideoCrf = CustomVideoCrf,
                VideoBitrate = CustomVideoBitrateBps
            };

            var codec = ResolveRecordingCodec();

            var ok = await _nativeWgcRecorder.StartAsync(segmentFile, _windowHandle, _fps, _includeCursor, codec)
                .ConfigureAwait(false);
            if (!ok)
            {
                if (_nativeWgcRecorder.TimedOutWaitingForFrame)
                {
                    MarkWgcUnusableThisSession();
                }

                LastStartError = _nativeWgcRecorder.LastErrorMessage ?? "WGC window session reported failure.";
                _nativeWgcRecorder.Dispose();
                _nativeWgcRecorder = null;
                return false;
            }

            LastStartWarning = _nativeWgcRecorder.LastWarningMessage ?? string.Empty;
            _lastSelectedVideoEncoderName = _nativeWgcRecorder.SelectedEncoderName ?? string.Empty;
            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            // First-frame/bring-up timeout surfaces as a thrown task here; check the no-frames cache on this path
            // too (the !ok branch is only reached on the rarer worker-completes-false race).
            if (_nativeWgcRecorder?.TimedOutWaitingForFrame == true)
            {
                MarkWgcUnusableThisSession();
            }

            LastStartError = ex.Message;
            Debug.WriteLine($"[RecordingService] WGC recorder start failed: {ex.Message}");
            _nativeWgcRecorder?.Dispose();
            _nativeWgcRecorder = null;
            return false;
        }
    }

    private async Task<bool> StartSeparateSegmentsAsync()
    {
        var codec = ResolveRecordingCodec();
        bool preferHw = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly;
        int segIndex = Math.Max(0, _segments.Count - 1);

        // Launch every window's WGC session CONCURRENTLY so their bring-up/first-frame timeouts overlap instead of
        // stacking sequentially — N windows would otherwise add N×~2s on the dual-monitor repro. See Fix A.
        var pending = new List<(int Index, VideoTrack Track, string SegPath, LibavWgcMkvSession Session, Task<bool> Task)>();
        for (int i = 0; i < _tracks.Count; i++)
        {
            var track = _tracks[i];
            track.Session?.Dispose();
            track.Session = null;

            string segPath = Path.Combine(_tempDir, $"track{i}_segment_{segIndex}.mkv");
            var session = new LibavWgcMkvSession
            {
                PreferHardwareEncoder = preferHw,
                VideoCrf = CustomVideoCrf,
                VideoBitrate = CustomVideoBitrateBps
            };
            pending.Add((i, track, segPath, session, session.StartAsync(segPath, track.Hwnd, _fps, _includeCursor, codec)));
        }

        int started = 0;
        foreach (var p in pending)
        {
            bool ok;
            try
            {
                ok = await p.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RecordingService] Track {p.Index} start threw: {ex.Message}");
                ok = false;
            }

            if (ok)
            {
                p.Track.Session = p.Session;
                p.Track.Segments.Add(p.SegPath);
                _lastSelectedVideoEncoderName = p.Session.SelectedEncoderName ?? _lastSelectedVideoEncoderName;
                started++;
            }
            else
            {
                if (p.Session.TimedOutWaitingForFrame)
                {
                    MarkWgcUnusableThisSession();
                }

                Debug.WriteLine($"[RecordingService] Track {p.Index} (hwnd {p.Track.Hwnd}) failed to start: {p.Session.LastErrorMessage}");
                p.Session.Dispose();
            }
        }

        if (started == 0)
        {
            LastStartError = "None of the selected windows could be captured.";
            return false;
        }

        if (started < _tracks.Count)
        {
            LastStartWarning = $"{_tracks.Count - started} window(s) could not be captured and were skipped.";
        }

        State = RecordingState.Recording;
        return true;
    }

    /// <summary>
    /// Separate-files fallback when WGC delivers no frames: capture each window's current screen rectangle (via
    /// gdigrab) into its own track file, so "every window to its own file" still works. Unlike WGC this does not
    /// follow the window if it moves and records whatever is on top, but it is GPU-agnostic and reliable. Sessions
    /// start concurrently. See docs/WGC_HANDOFF.md.
    /// </summary>
    private async Task<bool> StartSeparateGdigrabSegmentsAsync()
    {
        var codec = ResolveRecordingCodec();
        bool preferHw = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly;
        int segIndex = Math.Max(0, _segments.Count - 1);

        var pending = new List<(int Index, VideoTrack Track, string SegPath, LibavGdigrabMkvSession Session, Task<bool> Task)>();
        for (int i = 0; i < _tracks.Count; i++)
        {
            var track = _tracks[i];
            track.GdigrabSession?.Dispose();
            track.GdigrabSession = null;

            if (!TryGetWindowCaptureRect(track.Hwnd, out int x, out int y, out int w, out int h))
            {
                Debug.WriteLine($"[RecordingService] Track {i} (hwnd {track.Hwnd}) window rect unavailable; skipping.");
                continue;
            }

            string segPath = Path.Combine(_tempDir, $"track{i}_gdigrab_{segIndex}.mkv");
            var session = new LibavGdigrabMkvSession
            {
                HighlightCursor = _settingsService?.Settings.HighlightCursor ?? false,
                HighlightClicks = _settingsService?.Settings.HighlightClicks ?? false,
                ShowKeystrokes = _settingsService?.Settings.ShowKeystrokes ?? false,
                PreferHardwareEncoder = preferHw,
                VideoCrf = CustomVideoCrf,
                VideoBitrate = CustomVideoBitrateBps,
                // Pace output by wall-clock: N large window regions captured concurrently make gdigrab fall behind
                // the target fps, which with counter-based PTS produces a sped-up video. Wall-clock PTS keeps the
                // duration correct. (This also forces the single-threaded encode loop.)
                UseWallClockPts = true,
                // No webcam PiP here: a dshow webcam can't be opened by N concurrent sessions, and duplicating it
                // into every window file is undesirable.
            };
            AppLog.Information($"Wgc.SeparateFallback.Region track={i} hwnd=0x{track.Hwnd.ToInt64():X} rect={w}x{h}@({x},{y})");
            pending.Add((i, track, segPath, session, session.StartAsync(segPath, x, y, w, h, _fps, _includeCursor, codec)));
        }

        int started = 0;
        foreach (var p in pending)
        {
            bool ok;
            try
            {
                ok = await p.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RecordingService] Track {p.Index} gdigrab start threw: {ex.Message}");
                ok = false;
            }

            if (ok)
            {
                p.Track.GdigrabSession = p.Session;
                p.Track.Segments.Add(p.SegPath);
                _lastSelectedVideoEncoderName = p.Session.SelectedEncoderName ?? _lastSelectedVideoEncoderName;
                started++;
            }
            else
            {
                Debug.WriteLine($"[RecordingService] Track {p.Index} gdigrab failed: {p.Session.LastErrorMessage}");
                p.Session.Dispose();
            }
        }

        if (started == 0)
        {
            LastStartError = "None of the selected windows could be captured.";
            return false;
        }

        if (started < _tracks.Count)
        {
            LastStartWarning = $"{_tracks.Count - started} window(s) could not be captured and were skipped.";
        }

        State = RecordingState.Recording;
        return true;
    }

    /// <summary>
    /// Physical-pixel screen rectangle of a window, clamped to the virtual desktop and with even dimensions —
    /// suitable as gdigrab offset_x/offset_y/video_size. False if the window is gone or fully off-screen.
    /// The clamp is essential: gdigrab returns an I/O error if the capture rect extends past the desktop bounds,
    /// which happens for maximized windows whose borders sit a few px outside the monitor (verified on the
    /// dual-monitor repro: unclamped (-2056,-88) failed; clamped to the virtual desktop captured fine).
    /// </summary>
    private static bool TryGetWindowCaptureRect(IntPtr hwnd, out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r))
        {
            return false;
        }

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vRight = vx + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vBottom = vy + GetSystemMetrics(SM_CYVIRTUALSCREEN);

        int left = Math.Max(r.Left, vx);
        int top = Math.Max(r.Top, vy);
        int right = Math.Min(r.Right, vRight);
        int bottom = Math.Min(r.Bottom, vBottom);

        x = left;
        y = top;
        width = ((right - left) / 2) * 2;   // gdigrab/H.264 want even dimensions
        height = ((bottom - top) / 2) * 2;
        return width >= 2 && height >= 2;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private async Task<bool> StartGdigrabSegmentAsync(string segmentFile)
    {
        try
        {
            _nativeRecorder?.Dispose();
            _nativeRecorder = new LibavGdigrabMkvSession
            {
                EnableWebcam = _settingsService?.Settings.EnableWebcam ?? false,
                WebcamDeviceName = _settingsService?.Settings.WebcamDeviceName ?? string.Empty,
                WebcamCorner = _settingsService?.Settings.WebcamCorner ?? 3,
                WebcamWidthFraction = (_settingsService?.Settings.WebcamSize ?? 1) switch
                {
                    0 => 0.18f,
                    2 => 0.33f,
                    _ => 0.25f
                },
                WebcamCircular = _settingsService?.Settings.WebcamCircular ?? false,
                VideoCrf = CustomVideoCrf,
                VideoBitrate = CustomVideoBitrateBps,
                HighlightCursor = _settingsService?.Settings.HighlightCursor ?? false,
                HighlightClicks = _settingsService?.Settings.HighlightClicks ?? false,
                ShowKeystrokes = _settingsService?.Settings.ShowKeystrokes ?? false,
                PipelinedEncoding = _settingsService?.Settings.PipelinedEncoding ?? false,
                PreferHardwareEncoder = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly,
                // Always pace video PTS by wall-clock, not by a frame counter. The counter assumes every frame is
                // exactly 1/fps apart, so whenever gdigrab falls behind (large region, busy scene, loaded CPU) the
                // video timeline comes out SHORTER than real time — while the captured audio is real-time either
                // way. That difference is precisely the audio/video desync users hit on ordinary recordings, and it
                // grows the longer the recording runs. When capture does keep up, wall-clock and counter PTS agree,
                // so this is strictly the safer rule rather than a trade-off.
                UseWallClockPts = true
            };

            int x = (int)(_region.X * _visualScaling) + _screenOffset.X;
            int y = (int)(_region.Y * _visualScaling) + _screenOffset.Y;
            int w = ((int)(_region.Width * _visualScaling) / 2) * 2;
            int h = ((int)(_region.Height * _visualScaling) / 2) * 2;

            var codec = ResolveRecordingCodec();

            var ok = await _nativeRecorder.StartAsync(segmentFile, x, y, w, h, _fps, _includeCursor, codec)
                .ConfigureAwait(false);
            LastStartWarning = string.IsNullOrEmpty(LastStartWarning)
                ? _nativeRecorder.LastWarningMessage ?? string.Empty
                : LastStartWarning;
            _lastSelectedVideoEncoderName = _nativeRecorder.SelectedEncoderName ?? string.Empty;
            if (!ok)
            {
                LastStartError = _nativeRecorder.LastErrorMessage ?? "Native gdigrab session reported failure.";
                Debug.WriteLine($"[RecordingService] Native gdigrab session reported failure: {LastStartError}");
                _nativeRecorder.Dispose();
                _nativeRecorder = null;
                State = RecordingState.Idle;
                return false;
            }

            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            Debug.WriteLine($"[RecordingService] Native recorder start failed: {ex.Message}");
            _nativeRecorder?.Dispose();
            _nativeRecorder = null;
            State = RecordingState.Idle;
            return false;
        }
    }

    private async Task StopCurrentSegmentAsync()
    {
        bool anyTrackSession = _tracks.AsValueEnumerable().Any(t => t.Session != null || t.GdigrabSession != null);
        if (_nativeRecorder == null && _nativeWgcRecorder == null && _nativeWgcCompositeRecorder == null && !anyTrackSession)
        {
            StopAudioCapture();
            StopMicCapture();
            return;
        }

        try
        {
            if (_nativeWgcCompositeRecorder != null)
            {
                await StopWithTimeoutAsync(_nativeWgcCompositeRecorder.StopAsync(), "wgc-composite").ConfigureAwait(false);
            }

            if (_nativeWgcRecorder != null)
            {
                await StopWithTimeoutAsync(_nativeWgcRecorder.StopAsync(), "wgc-window").ConfigureAwait(false);
            }

            foreach (var track in _tracks)
            {
                if (track.Session != null)
                {
                    await StopWithTimeoutAsync(track.Session.StopAsync(), "wgc-track").ConfigureAwait(false);
                }

                if (track.GdigrabSession != null)
                {
                    // gdigrab stop doesn't wedge, but keep the timeout guard for symmetry/safety.
                    await StopWithTimeoutAsync(track.GdigrabSession.StopAsync(), "gdigrab-track").ConfigureAwait(false);
                }
            }

            if (_nativeRecorder != null)
            {
                await _nativeRecorder.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping native recorder: {ex.Message}");
        }
        finally
        {
            _nativeWgcCompositeRecorder?.Dispose();
            _nativeWgcCompositeRecorder = null;
            _nativeWgcRecorder?.Dispose();
            _nativeWgcRecorder = null;
            foreach (var track in _tracks)
            {
                track.Session?.Dispose();
                track.Session = null;
                track.GdigrabSession?.Dispose();
                track.GdigrabSession = null;
            }
            _nativeRecorder?.Dispose();
            _nativeRecorder = null;
            StopAudioCapture();
            StopMicCapture();
        }
    }

    /// <summary>
    /// Awaits a session stop with a hard timeout. If the stop doesn't complete in time we abandon the await
    /// (the caller's finally still Disposes the session) so the app can never hang unclosably; exceptions from
    /// a completed-but-faulted stop are still observed/propagated.
    /// </summary>
    private static async Task StopWithTimeoutAsync(Task stopTask, string label)
    {
        var finished = await Task.WhenAny(stopTask, Task.Delay(WgcStopTimeoutMs)).ConfigureAwait(false);
        if (finished != stopTask)
        {
            AppLog.Information($"Wgc.Stop.Timeout {label} did not stop within {WgcStopTimeoutMs}ms; abandoning to Dispose.");
            return;
        }

        await stopTask.ConfigureAwait(false);
    }
}
