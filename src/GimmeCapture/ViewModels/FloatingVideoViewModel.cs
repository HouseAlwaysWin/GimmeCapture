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

namespace GimmeCapture.ViewModels;

public class FloatingVideoViewModel : ViewModelBase, IDisposable
{
    private WriteableBitmap? _videoBitmap;
    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        set => this.RaiseAndSetIfChanged(ref _videoBitmap, value);
    }

    private Avalonia.Media.Color _borderColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    private double _borderThickness = 2.0;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private bool _isLooping = true;
    public bool IsLooping
    {
        get => _isLooping;
        set => this.RaiseAndSetIfChanged(ref _isLooping, value);
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    public System.Action? RequestRedraw { get; set; }

    private readonly string _videoPath;
    private readonly string _ffmpegPath;
    private CancellationTokenSource? _playCts;
    private readonly int _width;
    private readonly int _height;

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, Avalonia.Media.Color borderColor, double borderThickness)
    {
        _videoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _width = (width / 2) * 2; // Ensure even for FFmpeg
        _height = (height / 2) * 2;
        BorderColor = borderColor;
        BorderThickness = borderThickness;

        CloseCommand = ReactiveCommand.Create(() => 
        {
            Dispose();
            CloseAction?.Invoke();
        });

        // Initialize bitmap
        VideoBitmap = new WriteableBitmap(
            new PixelSize(_width, _height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        StartPlayback();
    }

    private void StartPlayback()
    {
        _playCts = new CancellationTokenSource();
        _ = PlaybackLoopFixed(_playCts.Token);
    }

    // Improved Playback with fixed-size frame reading
    private async Task PlaybackLoopFixed(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new MemoryStream();
                var frameSize = _width * _height * 4;
                var buffer = new byte[frameSize];

                var cmd = Cli.Wrap(_ffmpegPath)
                    .WithArguments($"-i \"{_videoPath}\" -f image2pipe -vcodec rawvideo -pix_fmt bgra -s {_width}x{_height} -tune zerolatency -loglevel quiet -")
                    .WithStandardOutputPipe(PipeTarget.ToStream(new FrameStreamWriter(this, frameSize)));

                await cmd.ExecuteAsync(ct);

                if (!IsLooping) break;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Playback Error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
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

    public void Dispose()
    {
        _playCts?.Cancel();
        _playCts?.Dispose();
        VideoBitmap?.Dispose();
    }
}
