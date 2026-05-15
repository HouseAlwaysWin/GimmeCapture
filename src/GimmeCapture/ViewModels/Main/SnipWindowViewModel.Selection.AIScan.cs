using Avalonia;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private Rect _detectedRect;
    public Rect DetectedRect
    {
        get => _detectedRect;
        set
        {
            this.RaiseAndSetIfChanged(ref _detectedRect, value);
            this.RaisePropertyChanged(nameof(IsAiDetectedRectPreviewVisible));
        }
    }

    /// <summary>
    /// Screenshot/recording: AI + Win32 window snap candidate boxes. Always false in translation (cursor / single / multi).
    /// </summary>
    public bool IsAiScanCandidateLayerVisible =>
        CurrentState == SnipState.Detecting && CurrentMode != SnipMode.Translation;

    /// <summary>
    /// Dashed hover preview when choosing a candidate. Always false in translation mode.
    /// </summary>
    public bool IsAiDetectedRectPreviewVisible =>
        CurrentMode != SnipMode.Translation && CurrentState == SnipState.Detecting && DetectedRect.Width > 0;

    public ObservableCollection<VisualRect> WindowRects { get; } = new();
    private readonly WindowDetectionService _detectionService = new();

    /// <summary>
    /// Clears screenshot/recording AI candidate boxes and hover preview. Not used in translation mode.
    /// </summary>
    private void ClearAiScanOverlayState()
    {
        DetectedRect = default;
        WindowRects.Clear();
        _scanCts?.Cancel();
    }

    public void RefreshWindowRects(IntPtr? excludeHWnd = null)
    {
        if (CurrentMode == SnipMode.Translation)
        {
            return;
        }

        // Get global rects (Physical pixels)
        var globalRects = _detectionService.GetVisibleWindowRects(excludeHWnd);
        
        // Translate to local coordinates based on ScreenOffset (Physical)
        // AND convert to logical coordinates by dividing by VisualScaling
        var localRects = globalRects
            .AsValueEnumerable()
            .Select(r => new VisualRect(
                (r.X - ScreenOffset.X) / VisualScaling, 
                (r.Y - ScreenOffset.Y) / VisualScaling, 
                r.Width / VisualScaling, 
                r.Height / VisualScaling));
        
        WindowRects.Clear();
        foreach (var rect in localRects)
        {
            WindowRects.Add(rect);
        }
    }

    public void UpdateDetectedRect(Point mousePos)
    {
        if (CurrentState != SnipState.Detecting || CurrentMode == SnipMode.Translation) return;
        
        // Convert VisualRects back to Rects for detection service (or update detection service)
        // Since VisualRect is simple, we can just project it.
        var rectList = WindowRects.AsValueEnumerable().Select(vr => new Rect(vr.X, vr.Y, vr.Width, vr.Height)).ToList();
        var rect = _detectionService.GetRectAtPoint(mousePos, rectList);
        
        DetectedRect = rect ?? new Rect(0,0,0,0);
    }

    private System.Threading.CancellationTokenSource? _scanCts;

    private async Task RunAIScanAsync()
    {
        if (CurrentMode == SnipMode.Translation) return;

        var engine = _mainVm?.AIScanEngine ?? AIScanEngine.OCR;
        if (engine == AIScanEngine.SAM2)
        {
            await RunSAM2ScanAsync();
            return;
        }

        await RunOCRScanAsync();
    }

    private async Task RunSAM2ScanAsync()
    {
        System.Diagnostics.Debug.WriteLine("[AI Scan] RunAIScanAsync started");

        // Don't run AI detection if we are actually recording (RecState is not Idle)
        // But ALLOW it if we are just in "Recording Mode" (preparing to record)
        if (RecState != RecordingState.Idle) 
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan] Abort: RecState is {RecState}");
            return;
        }

        if (CurrentMode == SnipMode.Translation)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan] Abort: Translation mode");
            return;
        }

        if (_mainVm == null || !_mainVm.EnableAI) 
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan] Abort: EnableAI is false or MainVm is null");
            return;
        }

        // USER REQUEST: This setting controls the SCANNING PROCESS itself.
        // If disabled, we do NOT run the expensive SAM2 detection.
        if (!_mainVm.EnableAIScan)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan] Abort: EnableAIScan is false");
            return;
        }

        if (_aiScanSessionService == null)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan] Abort: AI scan session service is null");
            return;
        }

        // Cancel previous scan if any
        _scanCts?.Cancel();
        _scanCts = new System.Threading.CancellationTokenSource();
        var token = _scanCts.Token;

        ShowTopLoadingBar = true;
        Console.WriteLine("[AI Scan] ShowTopLoadingBar set to TRUE");
        
        try
        {
            if (CurrentState == SnipState.Detecting)
            {
                ProcessingText = LocalizationService.Instance["StatusInitializingAI"] ?? "Initializing AI Models...";
            }

            var originalOpacity = MaskOpacity;
            MaskOpacity = 0;
            try
            {
                await Task.Delay(100, token);

                var progress = new Progress<AIScanStage>(stage =>
                {
                    if (CurrentState != SnipState.Detecting)
                    {
                        return;
                    }

                    ProcessingText = stage switch
                    {
                        AIScanStage.EncodingImage => LocalizationService.Instance["StatusAIEncoding"] ?? "AI Encoding Image...",
                        AIScanStage.DetectingObjects => LocalizationService.Instance["StatusAIDetecting"] ?? "Detecting Objects...",
                        _ => ProcessingText
                    };
                });

                var result = await _aiScanSessionService.RunScanAsync(CreateAIScanSessionRequest(AIScanEngine.SAM2), progress, token);
                if (!result.IsReady)
                {
                    if (CurrentState == SnipState.Detecting && !string.IsNullOrWhiteSpace(result.NotReadyStatusKey))
                    {
                        ProcessingText = LocalizationService.Instance[result.NotReadyStatusKey];
                        await Task.Delay(2000, token);
                    }

                    return;
                }

                ApplySam2ScanRects(result.DetectedRects, token);
            }
            finally
            {
                MaskOpacity = originalOpacity;
            }
        }
        catch (OperationCanceledException)
        {
             Console.WriteLine("[AI Scan] CANCELLED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI Scan] ERROR: {ex.Message}");
            Console.WriteLine($"[AI Scan] Stack: {ex.StackTrace}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            Console.WriteLine("[AI Scan] Finished");
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private async Task RunOCRScanAsync()
    {
        System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] RunOCRScanAsync started");

        if (RecState != RecordingState.Idle)
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan][OCR] Abort: RecState is {RecState}");
            return;
        }

        if (_mainVm == null || !_mainVm.EnableAI)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Abort: EnableAI is false or MainVm is null");
            return;
        }

        if (!_mainVm.EnableAIScan)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Abort: EnableAIScan is false");
            return;
        }

        if (CurrentMode == SnipMode.Translation)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Abort: Translation mode");
            return;
        }

        if (_aiScanSessionService == null)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Abort: AI scan session service is null");
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new System.Threading.CancellationTokenSource();
        var token = _scanCts.Token;

        ShowTopLoadingBar = true;

        try
        {
            if (CurrentState == SnipState.Detecting)
                ProcessingText = LocalizationService.Instance["StatusAIScanning"] ?? "AI Scanning...";

            var result = await _aiScanSessionService.RunScanAsync(CreateAIScanSessionRequest(AIScanEngine.OCR), ct: token);
            if (!result.IsReady)
            {
                System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] OCR resources are not ready");
                return;
            }

            ApplyOcrScanRects(result.DetectedRects, token);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] CANCELLED");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan][OCR] ERROR: {ex.Message}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private AIScanSessionRequest CreateAIScanSessionRequest(AIScanEngine engine)
    {
        return new AIScanSessionRequest(
            engine,
            new Rect(0, 0, ViewportSize.Width, ViewportSize.Height),
            ScreenOffset,
            VisualScaling,
            _mainVm?.AppSettingsService.Settings.SelectedSAM2Variant ?? SAM2Variant.Tiny,
            _mainVm?.SAM2GridDensity ?? 24,
            _mainVm?.SAM2MaxObjects ?? 20,
            _mainVm?.SAM2MinObjectSize ?? 20,
            _mainVm?.AppSettingsService.Settings.SourceLanguage ?? OCRLanguage.TraditionalChinese);
    }

    private void ApplySam2ScanRects(IReadOnlyList<Rect> rects, System.Threading.CancellationToken token)
    {
        if (!_mainVm!.ShowAIScanBox || rects.Count == 0)
        {
            Console.WriteLine(rects.Count == 0
                ? "[AI Scan] No objects detected"
                : $"[AI Scan] Complete: {rects.Count} objects detected (Hidden by setting)");
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            if (CurrentState != SnipState.Detecting || _mainVm?.EnableAI != true || CurrentMode == SnipMode.Translation) return;

            int addedCount = 0;
            foreach (var rect in rects)
            {
                if (token.IsCancellationRequested) break;
                WindowRects.Add(new VisualRect(rect));
                addedCount++;
            }

            Console.WriteLine($"[AI Scan] Complete: {addedCount} objects added (filtered from {rects.Count})");
        });
    }

    private void ApplyOcrScanRects(IReadOnlyList<Rect> rects, System.Threading.CancellationToken token)
    {
        if (!_mainVm!.ShowAIScanBox || rects.Count == 0)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            if (CurrentState != SnipState.Detecting || _mainVm?.EnableAI != true || CurrentMode == SnipMode.Translation) return;

            bool IsNearDuplicate(Rect a, Rect b)
            {
                const double posTolerance = 6.0;
                const double sizeTolerance = 8.0;
                return Math.Abs(a.X - b.X) <= posTolerance &&
                       Math.Abs(a.Y - b.Y) <= posTolerance &&
                       Math.Abs(a.Width - b.Width) <= sizeTolerance &&
                       Math.Abs(a.Height - b.Height) <= sizeTolerance;
            }

            foreach (var rect in rects)
            {
                bool duplicated = WindowRects.AsValueEnumerable().Any(existing =>
                    IsNearDuplicate(new Rect(existing.X, existing.Y, existing.Width, existing.Height), rect));
                if (!duplicated)
                {
                    WindowRects.Add(new VisualRect(rect));
                }
            }
        });
    }

    private Rect _activeScreenBounds = new Rect(0,0,1920,1080); // Default
    public Rect ActiveScreenBounds
    {
        get => _activeScreenBounds;
        set => this.RaiseAndSetIfChanged(ref _activeScreenBounds, value);
    }

    private ObservableCollection<ScreenBoundsViewModel> _allScreenBounds = new();
    public ObservableCollection<ScreenBoundsViewModel> AllScreenBounds
    {
        get => _allScreenBounds;
        set => this.RaiseAndSetIfChanged(ref _allScreenBounds, value);
    }

    // Command Declarations (Partial)
    public ReactiveCommand<Unit, Unit> AIScanCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> TriggerAutoScanCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAIScanBoxCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> TranslateAllSelectionsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ScanAllTextCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAllSelectionsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTranslationSelectCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAutoDetectCommand { get; set; } = null!;
    public ReactiveCommand<UserSelectionRect, Unit> RemoveUserSelectionCommand { get; set; } = null!;
    public ReactiveCommand<TranslationTool, Unit> SelectTranslationToolCommand { get; set; } = null!;
    public ReactiveCommand<object?, Unit> CopyTranslationTextCommand { get; set; } = null!;
    public ReactiveCommand<object?, Unit> PinTranslationCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> PinTranslationResultsCommand { get; set; } = null!;

    private void InitializeSelectionCommands()
    {
        AIScanCommand = ReactiveCommand.CreateFromTask(RunAIScanAsync);
        AIScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"AI Scan Command error: {ex}"));

        // Keep SAM2 path for manual/advanced trigger.
        TriggerAutoScanCommand = ReactiveCommand.CreateFromTask(RunSAM2ScanAsync);
        TriggerAutoScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Manual SAM2 Scan Command error: {ex}"));

        ToggleAIScanBoxCommand = ReactiveCommand.Create(() => { ShowAIScanBox = !ShowAIScanBox; return Unit.Default; });
        ToggleAIScanBoxCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Toggle AI Box error: {ex}"));


        var canExecuteInTranslation = this.WhenAnyValue(
            x => x.CurrentMode,
            x => x.IsInputFocused,
            (mode, textFocus) => mode == SnipMode.Translation && !textFocus);

        TranslateAllSelectionsCommand = ReactiveCommand.Create(() => { _ = TranslateAllSelectionsAsync(); }, canExecuteInTranslation);
        TranslateAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"TranslateAll error: {ex}"));

        ScanAllTextCommand = ReactiveCommand.CreateFromTask(ScanAllTextAsync, canExecuteInTranslation);
        ScanAllTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ScanAll error: {ex}"));

        ClearAllSelectionsCommand = ReactiveCommand.Create(() =>
        {
            UserSelections.Clear();
            if (CurrentTranslationTool == TranslationTool.Audio)
            {
                EnsureAudioTranslationBox();
            }
        }, canExecuteInTranslation);
        ClearAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ClearAll error: {ex}"));

        ToggleTranslationSelectCommand = ReactiveCommand.Create(() => { IsTranslationSelectionActive = !IsTranslationSelectionActive; }, canExecuteInTranslation);
        ToggleTranslationSelectCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ToggleSelect error: {ex}"));

        ToggleAutoDetectCommand = ReactiveCommand.Create(() => { IsGlobalAutoDetectEnabled = !IsGlobalAutoDetectEnabled; }, canExecuteInTranslation);
        ToggleAutoDetectCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ToggleAutoDetect error: {ex}"));
        
        SelectTranslationToolCommand = ReactiveCommand.Create<TranslationTool>(tool => 
        {
            CurrentTranslationTool = tool;
        });
        SelectTranslationToolCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Select Tool error: {ex}"));

        RemoveUserSelectionCommand = ReactiveCommand.Create<UserSelectionRect>(item =>
        {
            if (item == null) return;
            if (CurrentTranslationTool == TranslationTool.Audio && item.IsAudioPanel) return;
            UserSelections.Remove(item);
        });
        RemoveUserSelectionCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"RemoveSelection error: {ex}"));

        CopyTranslationTextCommand = ReactiveCommand.CreateFromTask<object?>(async item =>
        {
            var textToCopy = item switch
            {
                UserSelectionRect selection => !string.IsNullOrWhiteSpace(selection.TranslatedText)
                    ? selection.TranslatedText
                    : selection.OriginalText,
                TranslatedBlock block => !string.IsNullOrWhiteSpace(block.TranslatedText)
                    ? block.TranslatedText
                    : block.OriginalText,
                string rawText => rawText,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(textToCopy))
            {
                await _captureService.CopyToClipboardAsync(textToCopy);
                _mainVm?.SetStatus("StatusCopied");
            }
        });
        CopyTranslationTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Copy Translation error: {ex}"));

        PinTranslationCommand = ReactiveCommand.CreateFromTask<object?>(PinTranslationAsync);
        PinTranslationCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Pin Translation error: {ex}"));
        PinTranslationResultsCommand = ReactiveCommand.CreateFromTask(PinAllTranslationsAsync, canExecuteInTranslation);
        PinTranslationResultsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Pin All Translation error: {ex}"));

        // 監聽集合變更以即時更新遮罩挖空，並訂閱項目的屬性變更
        UserSelections.CollectionChanged += (s, e) => 
        {
            if (e.NewItems != null)
            {
                foreach (UserSelectionRect item in e.NewItems)
                {
                    item.WhenAnyValue(x => x.Bounds, x => x.IsTranslated)
                        .Subscribe(_ => UpdateMask());
                }
            }

            // Keep one persistent audio panel while in Audio mode.
            if (CurrentTranslationTool == TranslationTool.Audio && !UserSelections.AsValueEnumerable().Any(x => x.IsAudioPanel))
            {
                EnsureAudioTranslationBox();
            }

            UpdateMask();
        };
    }

    private void InitializeSAM2(MainWindowViewModel mainVm)
    {
        Task.Run(async () => 
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SAM2 Preload] Starting background initialization and warmup...");
                if (_aiScanSessionService != null)
                {
                    await _aiScanSessionService.WarmUpSam2Async();
                }
                System.Diagnostics.Debug.WriteLine("[SAM2 Preload] Background warmup complete. Ready for scan.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SAM2 Preload] Failed: {ex.Message}");
            }
        });
    }
}
