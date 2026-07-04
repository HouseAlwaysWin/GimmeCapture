using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Captures a PulseAudio source (system-audio monitor or microphone) via libavdevice's <c>pulse</c>
/// demuxer and writes a 16-bit PCM WAV — the Linux counterpart of the Windows NAudio WASAPI capture in
/// RecordingService.Audio.cs. The pulse demuxer delivers raw <c>pcm_s16le</c> packets, so we write the
/// packet payload straight into the WAV data chunk (no decode). Muting writes equal-length silence so the
/// WAV keeps the same duration and stays A/V-synced. One background read thread owns the format context.
/// </summary>
internal sealed unsafe class LinuxPulseAudioRecorder : IDisposable
{
    /// <summary>PulseAudio special source that maps to the default sink's monitor (system audio loopback).</summary>
    public const string DefaultMonitorSource = "@DEFAULT_MONITOR@";

    /// <summary>PulseAudio default source (typically the microphone).</summary>
    public const string DefaultSource = "default";

    private readonly string _source;
    private readonly string _wavPath;
    private readonly Func<bool> _isMuted;

    private AVFormatContext* _fmt;
    private FileStream? _wav;
    private long _dataBytes;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private Thread? _thread;
    private volatile bool _running;

    public LinuxPulseAudioRecorder(string source, string wavPath, Func<bool> isMuted)
    {
        _source = source;
        _wavPath = wavPath;
        _isMuted = isMuted;
    }

    /// <summary>Opens the source + WAV and starts capturing. Returns false if the source can't be opened.</summary>
    public bool Start()
    {
        try
        {
            FFmpegRuntime.EnsureInitialized();

            var ifmt = ffmpeg.av_find_input_format("pulse");
            if (ifmt == null)
            {
                AppLog.Warning("LinuxPulseAudio.Start", new InvalidOperationException("pulse demuxer missing (avdevice)."));
                return false;
            }

            AVFormatContext* fmt = null;
            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "sample_rate", "48000", 0);
            ffmpeg.av_dict_set(&opts, "channels", "2", 0);
            int r = ffmpeg.avformat_open_input(&fmt, _source, ifmt, &opts);
            ffmpeg.av_dict_free(&opts);
            if (r < 0 || fmt == null)
            {
                AppLog.Information($"LinuxPulseAudio.OpenFailed source={_source} err={r}");
                return false;
            }

            _fmt = fmt;
            int aidx = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (aidx < 0)
            {
                Cleanup();
                return false;
            }

            var par = _fmt->streams[aidx]->codecpar;
            _sampleRate = par->sample_rate > 0 ? par->sample_rate : 48000;
            _channels = par->ch_layout.nb_channels > 0 ? par->ch_layout.nb_channels : 2;

            _wav = File.Create(_wavPath);
            WriteWavHeaderPlaceholder();

            _running = true;
            _thread = new Thread(ReadLoop) { IsBackground = true, Name = "LinuxPulseAudio" };
            _thread.Start();
            AppLog.Information($"LinuxPulseAudio.Started source={_source} rate={_sampleRate} ch={_channels} -> {_wavPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxPulseAudio.Start", ex);
            Cleanup();
            return false;
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;

        try
        {
            if (_wav != null)
            {
                PatchWavSizes();
                _wav.Flush();
                _wav.Dispose();
                _wav = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxPulseAudio.Finalize", ex);
        }

        Cleanup();
    }

    private void ReadLoop()
    {
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        byte[] buffer = new byte[65536];
        try
        {
            while (_running)
            {
                int r = ffmpeg.av_read_frame(_fmt, pkt);
                if (r < 0)
                {
                    if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        Thread.Sleep(2);
                        continue;
                    }
                    break;
                }

                int size = pkt->size;
                if (size > 0 && _wav != null)
                {
                    if (buffer.Length < size)
                    {
                        buffer = new byte[size];
                    }

                    if (_isMuted())
                    {
                        Array.Clear(buffer, 0, size); // equal-length silence keeps A/V in sync
                    }
                    else
                    {
                        Marshal.Copy((IntPtr)pkt->data, buffer, 0, size);
                    }

                    _wav.Write(buffer, 0, size);
                    _dataBytes += size;
                }

                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxPulseAudio.ReadLoop", ex);
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }
    }

    private void WriteWavHeaderPlaceholder()
    {
        if (_wav == null)
        {
            return;
        }

        int byteRate = _sampleRate * _channels * 2;
        short blockAlign = (short)(_channels * 2);
        using var bw = new BinaryWriter(_wav, System.Text.Encoding.ASCII, leaveOpen: true);
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(0);                       // RIFF chunk size (patched on close)
        bw.Write(new[] { 'W', 'A', 'V', 'E' });
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16);                      // fmt chunk size
        bw.Write((short)1);                // PCM
        bw.Write((short)_channels);
        bw.Write(_sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)16);               // bits per sample
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(0);                       // data chunk size (patched on close)
    }

    private void PatchWavSizes()
    {
        if (_wav == null)
        {
            return;
        }

        long total = _wav.Length;
        _wav.Seek(4, SeekOrigin.Begin);
        WriteLE(_wav, (int)(total - 8));   // RIFF size = file - 8
        _wav.Seek(40, SeekOrigin.Begin);
        WriteLE(_wav, (int)_dataBytes);    // data size
        _wav.Seek(0, SeekOrigin.End);
    }

    private static void WriteLE(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BitConverter.TryWriteBytes(b, value);
        s.Write(b);
    }

    private void Cleanup()
    {
        if (_fmt != null)
        {
            AVFormatContext* fmt = _fmt;
            ffmpeg.avformat_close_input(&fmt);
            _fmt = null;
        }
    }
}
