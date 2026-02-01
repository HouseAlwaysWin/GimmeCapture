using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using System.ComponentModel;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using GimmeCapture.Models;
using System.Linq;
using GimmeCapture.Services;

namespace GimmeCapture.ViewModels;

public enum SnipState { Idle, Detecting, Selecting, Selected }

public class SnipWindowViewModel : ViewModelBase
{
    private SnipState _currentState = SnipState.Detecting;
    public SnipState CurrentState
    {
        get => _currentState;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentState, value);
            if (value == SnipState.Selected && AutoActionMode > 0 && AutoActionMode < 3)
            {
                TriggerAutoAction();
            }
        }
    }

    private bool _isRecordingMode;
    public bool IsRecordingMode
    {
        get => _isRecordingMode;
        set => this.RaiseAndSetIfChanged(ref _isRecordingMode, value);
    }

    // True when actively recording (not idle, not paused) - used to hide selection border
    public bool IsRecordingActive => _recordingService?.State == RecordingState.Recording;

    // Current recording format (gif, mp4, webm, etc.)
    public string RecordFormat => _mainVm?.RecordFormat ?? "mp4";

    private TimeSpan _recordingDuration = TimeSpan.Zero;
    public TimeSpan RecordingDuration
    {
        get => _recordingDuration;
        set 
        {
            this.RaiseAndSetIfChanged(ref _recordingDuration, value);
            this.RaisePropertyChanged(nameof(RecordingDurationText));
        }
    }

    public string RecordingDurationText => RecordingDuration.ToString(@"mm\:ss");

    private Avalonia.Threading.DispatcherTimer? _recordTimer;
    private readonly RecordingService? _recordingService;
    private readonly MainWindowViewModel? _mainVm;

    private PixelPoint _screenOffset;
    public PixelPoint ScreenOffset
    {
        get => _screenOffset;
        set => this.RaiseAndSetIfChanged(ref _screenOffset, value);
    }

    private Rect _detectedRect;
    public Rect DetectedRect
    {
        get => _detectedRect;
        set => this.RaiseAndSetIfChanged(ref _detectedRect, value);
    }

    public List<Rect> WindowRects { get; set; } = new();
    private readonly WindowDetectionService _detectionService = new();

    public void RefreshWindowRects(IntPtr? excludeHWnd = null)
    {
        // Get global rects
        var globalRects = _detectionService.GetVisibleWindowRects(excludeHWnd);
        
        // Translate to local coordinates based on ScreenOffset
        WindowRects = globalRects
            .Select(r => new Rect(r.X - ScreenOffset.X, r.Y - ScreenOffset.Y, r.Width, r.Height))
            .ToList();
    }

    public void UpdateDetectedRect(Point mousePos)
    {
        if (CurrentState != SnipState.Detecting) return;
        
        var rect = _detectionService.GetRectAtPoint(mousePos, WindowRects);
        
        // Simple heuristic: if the detected rect is basically the whole screen, might be the SnipWindow itself
        // or the desktop. We should be careful about selecting the SnipWindow.
        // But SnipWindow is newly created, should be fine if we filter by size or something if needed.
        
        DetectedRect = rect ?? new Rect(0,0,0,0);
    }

    private int _autoActionMode = 0; // 0=Normal, 1=Copy, 2=Pin
    public int AutoActionMode
    {
        get => _autoActionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoActionMode, value);
            if (value > 0 && CurrentState == SnipState.Selected)
            {
                TriggerAutoAction();
            }
        }
    }

    private void TriggerAutoAction()
    {
        if (AutoActionMode == 1) // Copy
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Copy());
        }
        else if (AutoActionMode == 2) // Pin
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Pin());
        }
    }

    private Size _viewportSize;
    public Size ViewportSize
    {
        get => _viewportSize;
        set 
        {
            this.RaiseAndSetIfChanged(ref _viewportSize, value);
            UpdateToolbarPosition();
        }
    }

    private double _toolbarTop;
    public double ToolbarTop
    {
        get => _toolbarTop;
        set => this.RaiseAndSetIfChanged(ref _toolbarTop, value);
    }

    private double _toolbarLeft;
    public double ToolbarLeft
    {
        get => _toolbarLeft;
        set => this.RaiseAndSetIfChanged(ref _toolbarLeft, value);
    }

    private Rect _selectionRect;
    public Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            UpdateMask();
            UpdateToolbarPosition();
        }
    }

    private void UpdateToolbarPosition()
    {
        // Use ViewportSize if available, otherwise assume a standard FHD height for initial positioning
        double vh = ViewportSize.Height > 0 ? ViewportSize.Height : 1080;
        double vw = ViewportSize.Width > 0 ? ViewportSize.Width : 1920;

        const double toolbarHeight = 35; // More accurate estimated height
        const double toolbarWidth = 400; // Estimated max width

        // Position below by default
        double top = SelectionRect.Bottom + 4; 
        double left = SelectionRect.Left;

        // If bottom overflows, position above selection
        if (top + toolbarHeight > vh - 10)
        {
            top = SelectionRect.Top - toolbarHeight - 4;
        }

        // Final safety clamps to keep toolbar within viewport
        if (top < 4) top = 4;
        if (top + toolbarHeight > vh - 4) top = vh - toolbarHeight - 4;

        if (left + toolbarWidth > vw - 10)
        {
            left = vw - toolbarWidth - 10;
        }
        if (left < 10) left = 10;

        ToolbarTop = top;
        ToolbarLeft = left;
    }

    private void UpdateMask()
    {
        MaskGeometry = new CombinedGeometry
        {
            GeometryCombineMode = GeometryCombineMode.Exclude,
            Geometry1 = new RectangleGeometry(new Rect(-10000, -10000, 20000, 20000)),
            Geometry2 = new RectangleGeometry(SelectionRect)
        };
    }

    private Geometry _maskGeometry = new GeometryGroup();
    public Geometry MaskGeometry
    {
        get => _maskGeometry;
        set => this.RaiseAndSetIfChanged(ref _maskGeometry, value);
    }

    private bool _isMagnifierEnabled = true;
    public bool IsMagnifierEnabled
    {
        get => _isMagnifierEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMagnifierEnabled, value);
    }

    private readonly Services.IScreenCaptureService _captureService;

    // Commands
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> PinCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    private readonly System.Collections.Generic.List<Annotation> _redoStack = new();
    private bool _isUndoingOrRedoing = false;

    private void Undo()
    {
        if (Annotations.Count > 0)
        {
            var item = Annotations[Annotations.Count - 1];
            _isUndoingOrRedoing = true;
            Annotations.RemoveAt(Annotations.Count - 1);
            _redoStack.Add(item);
            _isUndoingOrRedoing = false;
        }
    }

    private void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var item = _redoStack[_redoStack.Count - 1];
            _isUndoingOrRedoing = true;
            _redoStack.RemoveAt(_redoStack.Count - 1);
            Annotations.Add(item);
            _isUndoingOrRedoing = false;
        }
    }

    // Annotation Properties
    public ObservableCollection<Annotation> Annotations { get; } = new();

    private AnnotationType _currentTool = AnnotationType.None; // Default to None, no tool selected initially
    public AnnotationType CurrentTool
    {
        get => _currentTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsLineToolActive));
        }
    }

    public bool IsShapeToolActive 
    {
        get => CurrentTool == AnnotationType.Rectangle || CurrentTool == AnnotationType.Ellipse;
        set 
        {
            if (value)
            {
                // If turning ON but no sub-tool selected, force a notification to keep the button gray/unchecked
                if (CurrentTool != AnnotationType.Rectangle && CurrentTool != AnnotationType.Ellipse)
                {
                    this.RaisePropertyChanged(nameof(IsShapeToolActive));
                }
            }
            else
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
        }
    }

    public bool IsLineToolActive 
    {
        get => CurrentTool == AnnotationType.Arrow || CurrentTool == AnnotationType.Line || CurrentTool == AnnotationType.Pen;
        set
        {
            if (value)
            {
                if (CurrentTool != AnnotationType.Arrow && CurrentTool != AnnotationType.Line && CurrentTool != AnnotationType.Pen)
                {
                    this.RaisePropertyChanged(nameof(IsLineToolActive));
                }
            }
            else
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
        }
    }

    private Color _selectedColor = Colors.Red;
    public Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
    }

    private string _customHexColor = "#FF0000";
    public string CustomHexColor
    {
        get => _customHexColor;
        set => this.RaiseAndSetIfChanged(ref _customHexColor, value);
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

    private string _currentFontFamily = "Arial";
    public string CurrentFontFamily
    {
        get => _currentFontFamily;
        set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
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

    public ObservableCollection<string> AvailableFonts { get; } = new ObservableCollection<string>
    {
        "Arial", "Segoe UI", "Consolas", "Times New Roman", "Comic Sans MS", "Microsoft JhengHei", "Meiryo"
    };

    private bool _isDrawingMode = false;
    public bool IsDrawingMode
    {
        get => _isDrawingMode;
        set => this.RaiseAndSetIfChanged(ref _isDrawingMode, value);
    }

    private bool _isEnteringText = false;
    public bool IsEnteringText
    {
        get => _isEnteringText;
        set => this.RaiseAndSetIfChanged(ref _isEnteringText, value);
    }

    private Point _textInputPosition;
    public Point TextInputPosition
    {
        get => _textInputPosition;
        set => this.RaiseAndSetIfChanged(ref _textInputPosition, value);
    }

    private string _pendingText = string.Empty;
    public string PendingText
    {
        get => _pendingText;
        set => this.RaiseAndSetIfChanged(ref _pendingText, value);
    }

    private Color _selectionBorderColor = Colors.Red;
    public Color SelectionBorderColor
    {
        get => _selectionBorderColor;
        set => this.RaiseAndSetIfChanged(ref _selectionBorderColor, value);
    }

    private double _selectionBorderThickness = 2.0;
    public double SelectionBorderThickness
    {
        get => _selectionBorderThickness;
        set => this.RaiseAndSetIfChanged(ref _selectionBorderThickness, value);
    }

    private double _maskOpacity = 0.5;
    public double MaskOpacity
    {
        get => _maskOpacity;
        set => this.RaiseAndSetIfChanged(ref _maskOpacity, value);
    }

    public SnipWindowViewModel() : this(Colors.Red, 2.0, 0.5, null, null) { }

    public SnipWindowViewModel(Color borderColor, double borderThickness, double maskOpacity, RecordingService? recService = null, MainWindowViewModel? mainVm = null)
    {
        _captureService = new Services.ScreenCaptureService();
        _selectionBorderColor = borderColor;
        _selectionBorderThickness = borderThickness;
        _maskOpacity = maskOpacity;
        _recordingService = recService;
        _mainVm = mainVm;

        if (_recordingService != null)
        {
            _recordingService.WhenAnyValue(x => x.State)
                .Subscribe(_ => 
                {
                    this.RaisePropertyChanged(nameof(RecState));
                    this.RaisePropertyChanged(nameof(IsRecordingActive));
                });
        }

        CopyCommand = ReactiveCommand.CreateFromTask(Copy);
        SaveCommand = ReactiveCommand.CreateFromTask(Save);
        PinCommand = ReactiveCommand.CreateFromTask(Pin);
        CloseCommand = ReactiveCommand.Create(Close);

        ToggleModeCommand = ReactiveCommand.Create(() => 
        {
            if (RecState == RecordingState.Idle) IsRecordingMode = !IsRecordingMode;
        });

        StartRecordingCommand = ReactiveCommand.CreateFromTask(StartRecording);
        PauseRecordingCommand = ReactiveCommand.CreateFromTask(PauseRecording);
        StopRecordingCommand = ReactiveCommand.CreateFromTask(StopRecording);
        CopyRecordingCommand = ReactiveCommand.CreateFromTask(CopyRecording);

        Annotations.CollectionChanged += (s, e) =>
        {
            if (!_isUndoingOrRedoing)
            {
                _redoStack.Clear();
            }
        };

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(t => {
            if (CurrentTool == t)
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                // Force a reset to None first to ensure UI bindings correctly trigger and clear previous states
                CurrentTool = AnnotationType.None;
                CurrentTool = t;
                IsDrawingMode = true; 
            }
        });
        
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => SelectedColor = c);
        UndoCommand = ReactiveCommand.Create(Undo);
        RedoCommand = ReactiveCommand.Create(Redo);
        ClearCommand = ReactiveCommand.Create(() => Annotations.Clear());

        IncreaseThicknessCommand = ReactiveCommand.Create(() => { if (CurrentThickness < 20) CurrentThickness += 1; });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { if (CurrentThickness > 1) CurrentThickness -= 1; });
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize < 72) CurrentFontSize += 2; });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize > 8) CurrentFontSize -= 2; });
        
        ApplyHexColorCommand = ReactiveCommand.Create(() => 
        {
            try
            {
                var hex = CustomHexColor.TrimStart('#');
                if (hex.Length == 6)
                {
                    var r = Convert.ToByte(hex.Substring(0, 2), 16);
                    var g = Convert.ToByte(hex.Substring(2, 2), 16);
                    var b = Convert.ToByte(hex.Substring(4, 2), 16);
                    SelectedColor = Color.FromRgb(r, g, b);
                }
            }
            catch { }
        });

        ChangeLanguageCommand = ReactiveCommand.Create(() => LocalizationService.Instance.CycleLanguage());
        ToggleBoldCommand = ReactiveCommand.Create<Unit, bool>(_ => IsBold = !IsBold);
        ToggleItalicCommand = ReactiveCommand.Create<Unit, bool>(_ => IsItalic = !IsItalic);

        UpdateMask();
    }

    public RecordingState RecState => _recordingService?.State ?? RecordingState.Idle;

    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; }
    public ReactiveCommand<Unit, Unit> StartRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> StopRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRecordingCommand { get; }

    private async Task StartRecording()
    {
        if (_recordingService == null || _mainVm == null) return;
        
        string format = _mainVm.RecordFormat?.ToLowerInvariant() ?? "mp4";

        // Use local Temp folder in app directory instead of System Temp
        string tempDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
        try { System.IO.Directory.CreateDirectory(tempDir); } catch { }

        if (_mainVm.UseFixedRecordPath && !string.IsNullOrEmpty(_mainVm.VideoSaveDirectory))
        {
             // Ensure directory exists
             try { System.IO.Directory.CreateDirectory(_mainVm.VideoSaveDirectory); } catch { }
             string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";
             _currentRecordingPath = System.IO.Path.Combine(_mainVm.VideoSaveDirectory, fileName);
        }
        else
        {
             _currentRecordingPath = System.IO.Path.Combine(tempDir, $"GimmeCapture_{Guid.NewGuid()}.{format}");
        }
        
        var region = SelectionRect;
        
        // Ensure size is even for ffmpeg
        if (region.Width % 2 != 0) region = region.WithWidth(region.Width - 1);
        if (region.Height % 2 != 0) region = region.WithHeight(region.Height - 1);

        if (await _recordingService.StartAsync(region, _currentRecordingPath, format, _mainVm.ShowRecordCursor))
        {
            RecordingDuration = TimeSpan.Zero;
            
            _recordTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recordTimer.Tick += (s, e) => {
                if (RecState == RecordingState.Recording)
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1));
            };
            _recordTimer.Start();
        }
    }

    private async Task PauseRecording()
    {
        if (_recordingService == null) return;
        if (RecState == RecordingState.Recording) await _recordingService.PauseAsync();
        else if (RecState == RecordingState.Paused) await _recordingService.ResumeAsync();
    }

    private async Task StopRecording()
    {
        if (_recordingService == null || _mainVm == null) return;
        
        _recordTimer?.Stop();
        await _recordingService.StopAsync();

        // Use the actual output path from RecordingService (may have been modified during finalization)
        string? actualOutputPath = _recordingService.OutputFilePath ?? _currentRecordingPath;

        // Check if we need to prompt
        if (!_mainVm.UseFixedRecordPath && PickSaveFileAction != null && !string.IsNullOrEmpty(actualOutputPath))
        {
            if (System.IO.File.Exists(actualOutputPath))
            {
                var targetPath = await PickSaveFileAction();
                if (!string.IsNullOrEmpty(targetPath))
                {
                    try
                    {
                        if (System.IO.File.Exists(targetPath)) System.IO.File.Delete(targetPath);
                        System.IO.File.Move(actualOutputPath!, targetPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to move recording: {ex.Message}");
                    }
                }
                else
                {
                    // User cancelled, maybe delete temp? 
                    // Better to keep it in temp or delete? User might want it later.
                    // For now, let it be.
                }
            }
        }

        CloseAction?.Invoke();
    }

    private bool _isProcessingRecording = false;

    private async Task CopyRecording()
    {
        if (_isProcessingRecording || _recordingService == null || _mainVm == null) return;
        
        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();

            // RecordingService.StopAsync finishes internal tasks but file system might be lagging slightly
            // Or the path might miss extension because RecordingService handles it internally
            
            string? actualOutputPath = _recordingService.OutputFilePath ?? _currentRecordingPath;
            
            // Correction: RecordingService might add .mkv extension if missing but not update the property?
            // Let's check typical variants
            if (!string.IsNullOrEmpty(actualOutputPath) && !System.IO.File.Exists(actualOutputPath))
            {
               if (!actualOutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
               {
                   string withExt = actualOutputPath + ".mkv";
                   if (System.IO.File.Exists(withExt)) actualOutputPath = withExt;
               }
            }

            // Wait loop for existence (up to 2 seconds)
            if (!string.IsNullOrEmpty(actualOutputPath))
            {
                for (int i = 0; i < 20; i++) 
                {
                    if (System.IO.File.Exists(actualOutputPath)) break;
                    await Task.Delay(100);
                }
            }
            
            if (!string.IsNullOrEmpty(actualOutputPath) && System.IO.File.Exists(actualOutputPath))
            {
                try
                {
                    // Use PowerShell as the ultimate fallback for clipboard operations
                    // This bypasses all .NET threading/apartment/Avalonia issues
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-noprofile -command \"Set-Clipboard -Path '{actualOutputPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit(2000); // Wait up to 2 seconds
                    
                    // Success, no message required as per user request
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to copy recording to clipboard: {ex.Message}");
                    System.Windows.Forms.MessageBox.Show($"Failed to copy: {ex.Message}", "Error");
                }
            }
            else 
            {
                 // Debugging help: show what path was looked for
                 System.Windows.Forms.MessageBox.Show($"Video file not found at:\n{actualOutputPath}", "Error");
            }

            CloseAction?.Invoke();
        }
        finally
        {
            _isProcessingRecording = false;
        }
    }

    private async Task PinRecording()
    {
        if (_isProcessingRecording || _recordingService == null) return;
        
        // Capture state BEFORE stopping, because StopAsync sets it to Idle
        bool wasRecording = _recordingService.State == RecordingState.Recording;

        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();
            
            // ... (screenshot logic omitted for brevity if irrelevant, keeping existing structure)
            
            // Start playing the video with ffplay (frameless, on top)
            if (wasRecording) // Use captured state
            {
                 // Stop already called above
                 
                 var recordingPath = _recordingService.LastRecordingPath;
                 if (string.IsNullOrEmpty(recordingPath) || !System.IO.File.Exists(recordingPath)) 
                 {
                     System.Windows.Forms.MessageBox.Show($"找不到錄影檔案: {recordingPath}", "錯誤");
                     return;
                 }

                 // Use path from service (checks system path too)
                 var ffplayPath = _recordingService.Downloader.GetFFplayPath();
                 
                 // Fallback check
                 if (string.IsNullOrEmpty(ffplayPath) || !System.IO.File.Exists(ffplayPath))
                 {
                     System.Windows.Forms.MessageBox.Show(
                        $"找不到播放器組件 (ffplay.exe)。\n請確認是否已安裝或下載完成。", 
                        "組件缺失");
                    _isProcessingRecording = false;
                     return;
                 }

                 // Calculate Geometry
                 // ffplay expects integer coordinates
                 int x = (int)(SelectionRect.X + ScreenOffset.X);
                 int y = (int)(SelectionRect.Y + ScreenOffset.Y);
                 int w = (int)SelectionRect.Width;
                 int h = (int)SelectionRect.Height;
                 
                 // Create and show FloatingVideoWindow instead of raw ffplay
                 Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                 {
                     var settings = new AppSettingsService();
                     settings.LoadSync(); // Simple load
                     
                     var videoVm = new FloatingVideoViewModel(
                         recordingPath, 
                         ffplayPath.Replace("ffplay.exe", "ffmpeg.exe"), // We need ffmpeg for streaming
                         w, h, 
                         SelectionBorderColor, 
                         SelectionBorderThickness,
                         settings.Settings.ShowPinDecoration,
                         settings.Settings.HidePinBorder);
                         
                     var videoWin = new Views.FloatingVideoWindow
                     {
                         DataContext = videoVm,
                         Width = w + 20, // Add margin from XAML
                         Height = h + 20,
                         Position = new PixelPoint(x - 10, y - 10)
                     };
                     
                     videoWin.Show();
                 });
                 
                 CloseAction?.Invoke();
            }
        }
        finally
        {
            _isProcessingRecording = false;
        }
    }

    private string? _currentRecordingPath;

    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyHexColorCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeLanguageCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleBoldCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleItalicCommand { get; }

    public static class StaticData
    {
        public static Color[] ColorsList { get; } = new[]
        {
            Colors.Red, Colors.Green, Colors.Blue, 
            Colors.Yellow, Colors.Cyan, Colors.Magenta,
            Colors.White, Colors.Black, Colors.Gray
        };
    }

    private async Task Copy() 
    { 
        // If recording is processing, ignore copy command to prevent overwriting with screenshot
        if (_isProcessingRecording) return;

        // If recording is active, copy recording instead of screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await CopyRecording();
            return;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try 
            {
                var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, Annotations, _mainVm?.ShowSnipCursor ?? false);
                await _captureService.CopyToClipboardAsync(bitmap);
            }
            finally
            {
                CloseAction?.Invoke();
            }
        }
    }

    private async Task Save() 
    { 
         // If recording is active, stop recording instead of saving screenshot
         if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
         {
             await StopRecording();
             return;
         }

         if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
         {
             HideAction?.Invoke();
             await Task.Delay(200); // Wait for UI update

             try
             {
                 var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, Annotations, _mainVm?.ShowSnipCursor ?? false);
                 
                 if (_mainVm != null && _mainVm.AutoSave)
                 {
                     var dir = _mainVm.SaveDirectory;
                     if (string.IsNullOrEmpty(dir))
                     {
                         dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "GimmeCapture");
                     }
                     try { System.IO.Directory.CreateDirectory(dir); } catch { }

                     var fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                     var path = System.IO.Path.Combine(dir, fileName);
                     await _captureService.SaveToFileAsync(bitmap, path);
                     System.Diagnostics.Debug.WriteLine($"Auto-saved to {path}");
                 }
                 else if (PickSaveFileAction != null)
                 {
                     var path = await PickSaveFileAction.Invoke();
                     if (!string.IsNullOrEmpty(path))
                     {
                        await _captureService.SaveToFileAsync(bitmap, path);
                        System.Diagnostics.Debug.WriteLine($"Saved to {path}");
                     }
                 }
                 else
                 {
                     // Fallback
                     var fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                     var path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), fileName);
                     await _captureService.SaveToFileAsync(bitmap, path);
                 }
             }
             finally
             {
                 CloseAction?.Invoke(); 
             }
         }
    }
    
    private async Task Pin()
    {
        // If recording is active, pin recording instead of screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await PinRecording();
            return;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update
            
            try
            {
                var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, Annotations, _mainVm?.ShowSnipCursor ?? false);
                
                // Convert SKBitmap to Avalonia Bitmap
                using var image = SkiaSharp.SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var stream = new System.IO.MemoryStream();
                data.SaveTo(stream);
                stream.Position = 0;
                
                var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                
                // Open Floating Window
            OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness);
            }
            finally
            {
                CloseAction?.Invoke();
            }
        }
    }

    private void Close() { CloseAction?.Invoke(); }
    
    public Action? CloseAction { get; set; }
    public Action? HideAction { get; set; }
    public Func<Task<string?>>? PickSaveFileAction { get; set; }
    public System.Action<Bitmap, Rect, Color, double>? OpenPinWindowAction { get; set; }
}
