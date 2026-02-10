using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GimmeCapture.Models;
using CliWrap;
using CliWrap.Buffered;
using System.Linq;
using System.Reactive.Linq;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Floating;

public class FloatingVideoViewModel : ViewModelBase, IDisposable, IDrawingToolViewModel
{
    public bool ShowIconSettings => false;
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; } 
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }

    public System.Collections.Generic.IEnumerable<Avalonia.Media.Color> PresetColors => GimmeCapture.ViewModels.Main.SnipWindowViewModel.StaticData.ColorsList;
    public ReactiveCommand<Avalonia.Media.Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }


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

    // Added properties for Resize and Toolbar logic
    private double _displayWidth;
    public double DisplayWidth
    {
        get => _displayWidth;
        set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
    }

    private double _displayHeight;
    public double DisplayHeight
    {
        get => _displayHeight;
        set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
    }

    private bool _isEnteringText;
    public bool IsEnteringText
    {
        get => _isEnteringText;
        set => this.RaiseAndSetIfChanged(ref _isEnteringText, value);
    }

    private string _pendingText = string.Empty;
    public string PendingText
    {
        get => _pendingText;
        set => this.RaiseAndSetIfChanged(ref _pendingText, value);
    }

    private Avalonia.Point _textInputPosition;
    public Avalonia.Point TextInputPosition
    {
        get => _textInputPosition;
        set => this.RaiseAndSetIfChanged(ref _textInputPosition, value);
    }

    private string _currentFontFamily = "Arial";
    public string CurrentFontFamily
    {
        get => _currentFontFamily;
        set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
    }

    public ObservableCollection<double> Thicknesses { get; } = new() { 1, 2, 4, 6, 8, 12, 16, 24 };

    public ObservableCollection<string> AvailableFonts { get; } = new ObservableCollection<string>
    {
        "Arial", "Segoe UI", "Consolas", "Times New Roman", "Comic Sans MS", "Microsoft JhengHei", "Meiryo"
    };

    private FloatingTool _currentTool = FloatingTool.None;
    public FloatingTool CurrentTool
    {
        get => _currentTool;
        set 
        {
            if (_currentTool == value) return;
            
            if (value != FloatingTool.None)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }

            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(IsSelectionMode));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set 
        {
            if (_currentAnnotationTool == value) return;
            
            if (value != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }

            this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

    public ObservableCollection<Annotation> Annotations { get; } = new();

    public bool IsShapeToolActive => CurrentAnnotationTool == AnnotationType.Rectangle || CurrentAnnotationTool == AnnotationType.Ellipse || CurrentAnnotationTool == AnnotationType.Arrow || CurrentAnnotationTool == AnnotationType.Line;
    public bool IsPenToolActive => CurrentAnnotationTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentAnnotationTool == AnnotationType.Text;

    // Explicit interface implementation to resolve name clash


    public System.Action FocusWindowAction { get; set; } = () => { };

    private Avalonia.Media.Color _selectedColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
    }

    private double _currentThickness = 2.0;
    public double CurrentThickness
    {
        get => _currentThickness;
        set => this.RaiseAndSetIfChanged(ref _currentThickness, value);
    }

    private double _currentFontSize = 24.0;
    public double CurrentFontSize
    {
        get => _currentFontSize;
        set => this.RaiseAndSetIfChanged(ref _currentFontSize, value);
    }

    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set => this.RaiseAndSetIfChanged(ref _isBold, value);
    }

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set => this.RaiseAndSetIfChanged(ref _isItalic, value);
    }

    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : (CurrentTool == FloatingTool.Selection ? FloatingTool.None : CurrentTool);
    }

    public bool IsAnyToolActive => CurrentTool != FloatingTool.None || CurrentAnnotationTool != AnnotationType.None;

    private Avalonia.Rect _selectionRect = new Avalonia.Rect();
    public Avalonia.Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            this.RaisePropertyChanged(nameof(IsSelectionActive));
        }
    }

    public bool IsSelectionActive => SelectionRect.Width > 0 && SelectionRect.Height > 0;

    public ReactiveCommand<Unit, Unit> SelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> CropCommand { get; } // Future implementation
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; } // Future implementation
    
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; }
    
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; }

    
    // Dependencies
    private readonly GimmeCapture.Services.Abstractions.IClipboardService _clipboardService;
    public GimmeCapture.Services.Abstractions.IClipboardService ClipboardService => _clipboardService;

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, GimmeCapture.Services.Abstractions.IClipboardService clipboardService)
    {
        VideoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _width = (width / 2) * 2; // Ensure even for FFmpeg
        _height = (height / 2) * 2;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DisplayWidth = originalWidth;
        DisplayHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;

        CloseCommand = ReactiveCommand.Create(() => 
        {
            Dispose();
            CloseAction?.Invoke();
        });

        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });

        // shared drawing commands
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Min(CurrentFontSize + 2, 72); });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Max(CurrentFontSize - 2, 8); });
        ChangeColorCommand = ReactiveCommand.Create<Avalonia.Media.Color>(c => SelectedColor = c);
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Min(CurrentThickness + 1, 30); });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Max(CurrentThickness - 1, 1); });

        SelectionCommand = ReactiveCommand.Create(() => 
        {
            CurrentTool = CurrentTool == FloatingTool.Selection ? FloatingTool.None : FloatingTool.Selection;
        });

        // Placeholders for now, logic to be implemented if video cropping/re-pinning is verified feasible
        CropCommand = ReactiveCommand.Create(() => { });
        PinSelectionCommand = ReactiveCommand.Create(() => { });

        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (Annotations.Count > 0)
            {
                var flattened = await GetFlattenedBitmapAsync();
                if (flattened != null)
                {
                    await _clipboardService.CopyImageAsync(flattened);
                    return;
                }
            }
            
            if (CopyAction != null) await CopyAction();
        });

        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (SaveAction != null) await SaveAction();
        });

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(tool => 
        {
            CurrentAnnotationTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
        });

        ToggleToolGroupCommand = ReactiveCommand.Create<string>(group => 
        {
             if (group == "Shapes")
             {
                 if (IsShapeToolActive) CurrentAnnotationTool = AnnotationType.None;
                 else CurrentAnnotationTool = AnnotationType.Rectangle;
             }
             else if (group == "Pen")
             {
                 CurrentAnnotationTool = (CurrentAnnotationTool == AnnotationType.Pen) ? AnnotationType.None : AnnotationType.Pen;
             }
             else if (group == "Text")
             {
                 if (IsTextToolActive) CurrentAnnotationTool = AnnotationType.None;
                 else CurrentAnnotationTool = AnnotationType.Text;
             }
        });

        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations);


        var canUndo = this.WhenAnyValue(x => x.HasUndo).ObserveOn(RxApp.MainThreadScheduler);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);

        var canRedo = this.WhenAnyValue(x => x.HasRedo).ObserveOn(RxApp.MainThreadScheduler);
        RedoCommand = ReactiveCommand.Create(Redo, canRedo);

        ConfirmTextEntryCommand = ReactiveCommand.Create(() => 
        {
            if (!string.IsNullOrWhiteSpace(PendingText))
            {
                AddAnnotation(new Annotation
                {
                    Type = AnnotationType.Text,
                    StartPoint = TextInputPosition,
                    EndPoint = TextInputPosition,
                    Text = PendingText,
                    Color = SelectedColor,
                    FontSize = CurrentFontSize,
                    FontFamily = CurrentFontFamily,
                    IsBold = IsBold,
                    IsItalic = IsItalic
                });
            }
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });

        CancelTextEntryCommand = ReactiveCommand.Create(() => 
        {
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
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

    private Stack<IHistoryAction> _historyStack = new();
    private Stack<IHistoryAction> _redoHistoryStack = new();

    private bool _hasUndo;
    public bool HasUndo
    {
        get => _hasUndo;
        set => this.RaiseAndSetIfChanged(ref _hasUndo, value);
    }

    private bool _hasRedo;
    public bool HasRedo
    {
        get => _hasRedo;
        set => this.RaiseAndSetIfChanged(ref _hasRedo, value);
    }

    public bool CanUndo => HasUndo;
    public bool CanRedo => HasRedo;

    public Action<Avalonia.PixelPoint, double, double>? RequestSetWindowRect { get; set; }

    public void PushUndoAction(IHistoryAction action)
    {
        _historyStack.Push(action);
        _redoHistoryStack.Clear();
        UpdateHistoryStatus();
    }

    public void PushResizeAction(Avalonia.PixelPoint oldPos, double oldW, double oldH, Avalonia.PixelPoint newPos, double newW, double newH)
    {
        if (oldPos == newPos && oldW == newW && oldH == newH) return;
        
        PushUndoAction(new WindowTransformHistoryAction(
            (pos, w, h) => RequestSetWindowRect?.Invoke(pos, w, h),
            oldPos, oldW, oldH,
            newPos, newW, newH));
    }

    private void Undo()
    {
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        action.Undo();
        _redoHistoryStack.Push(action);
        UpdateHistoryStatus();
    }

    private void Redo()
    {
        if (_redoHistoryStack.Count == 0) return;
        var action = _redoHistoryStack.Pop();
        action.Redo();
        _historyStack.Push(action);
        UpdateHistoryStatus();
    }

    private void UpdateHistoryStatus()
    {
        HasUndo = _historyStack.Count > 0;
        HasRedo = _redoHistoryStack.Count > 0;
    }

    public void AddAnnotation(Annotation annotation)
    {
        Annotations.Add(annotation);
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, true));
    }

    private void ClearAnnotations()
    {
        if (Annotations.Count == 0) return;
        PushUndoAction(new ClearAnnotationsHistoryAction(Annotations));
        Annotations.Clear();
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


    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (VideoBitmap == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                // 1. Snapshot the current WriteableBitmap
                // Since WriteableBitmap is accessed by UI thread and background thread (via IL), we need to be careful.
                // But typically we can just Lock and Copy.
                
                using var locked = VideoBitmap.Lock();
                var info = new SkiaSharp.SKImageInfo(VideoBitmap.PixelSize.Width, VideoBitmap.PixelSize.Height, SkiaSharp.SKColorType.Bgra8888);
                using var skBitmap = new SkiaSharp.SKBitmap(info);
                
                unsafe 
                {
                    // Copy pixels directly
                    long len = (long)info.BytesSize;
                    Buffer.MemoryCopy((void*)locked.Address, (void*)skBitmap.GetPixels(), len, len);
                }
                
                // 2. Create surface
                using var surface = SkiaSharp.SKSurface.Create(info);
                using var canvas = surface.Canvas;
                
                // 3. Draw base image
                canvas.DrawBitmap(skBitmap, 0, 0);
                
                // 4. Draw Annotations
                // Coordinate mapping specific to Video which might be scaled in UI?
                // `DisplayWidth` / `DisplayHeight` vs `_width` / `_height` (which are actual video dimensions).
                
                // If DisplayWidth == _width, scale is 1.
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)_width / refW; 
                var scaleY = (double)_height / refH;
                
                foreach (var ann in Annotations)
                {
                    var paint = new SkiaSharp.SKPaint
                    {
                        Color = new SkiaSharp.SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                        StrokeWidth = (float)(ann.Thickness * scaleX),
                        IsAntialias = true,
                        Style = SkiaSharp.SKPaintStyle.Stroke
                    };
                    
                    if (ann.Type == AnnotationType.Pen)
                    {
                        paint.StrokeCap = SkiaSharp.SKStrokeCap.Round;
                        paint.StrokeJoin = SkiaSharp.SKStrokeJoin.Round;
                    }

                    switch (ann.Type)
                    {
                        case AnnotationType.Rectangle:
                        case AnnotationType.Ellipse:
                            var rect = new SkiaSharp.SKRect(
                                (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                                (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                                (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                                (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                            
                            if (ann.Type == AnnotationType.Rectangle)
                                canvas.DrawRect(rect, paint);
                            else
                                canvas.DrawOval(rect, paint);
                            break;
                            
                        case AnnotationType.Line:
                            canvas.DrawLine(
                                (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY),
                                (float)(ann.EndPoint.X * scaleX), (float)(ann.EndPoint.Y * scaleY),
                                paint);
                            break;
                            
                        case AnnotationType.Arrow:
                            // Draw Line
                            float x1 = (float)(ann.StartPoint.X * scaleX);
                            float y1 = (float)(ann.StartPoint.Y * scaleY);
                            float x2 = (float)(ann.EndPoint.X * scaleX);
                            float y2 = (float)(ann.EndPoint.Y * scaleY);
                            canvas.DrawLine(x1, y1, x2, y2, paint);
                            
                            // Draw Arrowhead
                            double angle = Math.Atan2(y2 - y1, x2 - x1);
                            double arrowLen = 15 * scaleX; 
                            double arrowAngle = Math.PI / 6;
                            
                            float ax1 = (float)(x2 - arrowLen * Math.Cos(angle - arrowAngle));
                            float ay1 = (float)(y2 - arrowLen * Math.Sin(angle - arrowAngle));
                            float ax2 = (float)(x2 - arrowLen * Math.Cos(angle + arrowAngle));
                            float ay2 = (float)(y2 - arrowLen * Math.Sin(angle + arrowAngle));
                            
                            var path = new SkiaSharp.SKPath();
                            path.MoveTo(x2, y2);
                            path.LineTo(ax1, ay1);
                            path.LineTo(ax2, ay2);
                            path.Close();
                            
                            paint.Style = SkiaSharp.SKPaintStyle.Fill;
                            canvas.DrawPath(path, paint);
                            break;

                         case AnnotationType.Pen:
                             // Snapshot points to avoid concurrent modification issues and use DrawPoints
                             if (ann.Points.Count > 1)
                             {
                                 var points = ann.Points.Select(p => new SkiaSharp.SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                                 if (points.Length > 1)
                                 {
                                     canvas.DrawPoints(SkiaSharp.SKPointMode.Polygon, points, paint);
                                 }
                             }
                             break;
                             
                         case AnnotationType.Text:
                             var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, (float)(ann.FontSize * scaleX));
                             var textPaint = new SkiaSharp.SKPaint
                             {
                                 Color = paint.Color,
                                 IsAntialias = true,
                             };
                             canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SkiaSharp.SKTextAlign.Left, font, textPaint);
                             break;
                    }
                }
                
                // 5. Export
                using var image = surface.Snapshot();
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var resultMs = new System.IO.MemoryStream();
                data.SaveTo(resultMs);
                resultMs.Position = 0;
                
                return new Bitmap(resultMs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error flattening video frame: {ex}");
                return null;
            }
        });
    }
}
