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

namespace GimmeCapture.ViewModels.Floating;

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

    private bool _hidePinDecoration = false;
    public bool HidePinDecoration
    {
        get => _hidePinDecoration;
        set
        {
            this.RaiseAndSetIfChanged(ref _hidePinDecoration, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
    }

    private bool _isLooping = true;
    public bool IsLooping
    {
        get => _isLooping;
        set => this.RaiseAndSetIfChanged(ref _isLooping, value);
    }

    private double _originalWidth;
    public double OriginalWidth
    {
        get => _originalWidth;
        set => this.RaiseAndSetIfChanged(ref _originalWidth, value);
    }

    private double _originalHeight;
    public double OriginalHeight
    {
        get => _originalHeight;
        set => this.RaiseAndSetIfChanged(ref _originalHeight, value);
    }

    private bool _showToolbar = false;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set => this.RaiseAndSetIfChanged(ref _showToolbar, value);
    }

    private double _wingScale = 1.0;
    public double WingScale
    {
        get => _wingScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _wingScale, value);
            this.RaisePropertyChanged(nameof(WingWidth));
            this.RaisePropertyChanged(nameof(WingHeight));
            this.RaisePropertyChanged(nameof(LeftWingMargin));
            this.RaisePropertyChanged(nameof(RightWingMargin));
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private double _cornerIconScale = 1.0;
    public double CornerIconScale
    {
        get => _cornerIconScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _cornerIconScale, value);
            this.RaisePropertyChanged(nameof(SelectionIconSize));
        }
    }

    // Derived properties for UI binding
    public double WingWidth => 100 * WingScale;
    public double WingHeight => 60 * WingScale;
    public double SelectionIconSize => 22 * CornerIconScale;
    public Avalonia.Thickness LeftWingMargin => new Avalonia.Thickness(-WingWidth, 0, 0, 0);
    public Avalonia.Thickness RightWingMargin => new Avalonia.Thickness(0, 0, -WingWidth, 0);

    public Avalonia.Thickness WindowPadding
    {
        get
        {
            // If decorations are hidden, we just need the standard margin (e.g. 10 for shadow/resize handles).
            // If they are visible, we need enough space for the wings (WingWidth).
            double hPad = _hidePinDecoration ? 10 : System.Math.Max(10, WingWidth);
            double vPad = 10;
            return new Avalonia.Thickness(hPad, vPad, hPad, vPad);
        }
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    public System.Action? RequestRedraw { get; set; }
    public System.Func<Task>? CopyAction { get; set; }
    public System.Func<Task>? SaveAction { get; set; }

    public string VideoPath { get; }
    private readonly string _ffmpegPath;
    private CancellationTokenSource? _playCts;
    private readonly int _width;
    private readonly int _height;

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder)
    {
        VideoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _width = (width / 2) * 2; // Ensure even for FFmpeg
        _height = (height / 2) * 2;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;

        CloseCommand = ReactiveCommand.Create(() => 
        {
            Dispose();
            CloseAction?.Invoke();
        });

        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });

        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CopyAction != null) await CopyAction();
        });

        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (SaveAction != null) await SaveAction();
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
                    .WithArguments($"-re -i \"{VideoPath}\" -f image2pipe -vcodec rawvideo -pix_fmt bgra -s {_width}x{_height} -sws_flags lanczos -loglevel quiet -")
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
