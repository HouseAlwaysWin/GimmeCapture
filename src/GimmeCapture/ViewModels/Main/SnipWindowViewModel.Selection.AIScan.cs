using Avalonia;
using Avalonia.Threading;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interaction;
using GimmeCapture.Services.OCR;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private readonly HoverRectangleAnimationController _hoverAnimationController = new(TimeSpan.FromMilliseconds(120), dashSpeed: -20.0);
    private readonly DispatcherTimer _hoverAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private IReadOnlyList<WindowCandidate> _windowCandidates = [];
    private WindowCandidate? _currentHoverCandidate;
    private OcrCandidate? _currentHoverOcrCandidate;
    private IReadOnlyList<Rect> _ocrRawRects = [];
    private IReadOnlyList<OcrCandidate> _ocrLineCandidates = [];
    private IReadOnlyList<OcrCandidate> _ocrParagraphCandidates = [];
    private IReadOnlyList<Rect> _translationOcrRawRects = [];
    private IReadOnlyList<OcrCandidate> _translationOcrLineCandidates = [];
    private IReadOnlyList<OcrCandidate> _translationOcrParagraphCandidates = [];
    private OcrCandidate? _currentHoverTranslationOcrCandidate;
    private Rect _translationOcrCacheViewportRect;
    private PixelPoint _translationOcrCacheScreenOffset;
    private double _translationOcrCacheVisualScaling = -1;
    private bool _translationOcrCacheValid;
    private bool _isTranslationOcrSearchActive;
    private bool _isTranslationOcrScanRunning;
    private string? _currentHoverKey;
    private DateTime _lastHoverAnimationTickUtc;

    private Rect _hoverTargetRect;
    public Rect HoverTargetRect
    {
        get => _hoverTargetRect;
        private set => this.RaiseAndSetIfChanged(ref _hoverTargetRect, value);
    }

    /// <summary>
    /// Screenshot/recording AI candidate boxes, plus OCR raw rect debug overlay in translation mode.
    /// </summary>
    public bool IsAiScanCandidateLayerVisible =>
        _mainVm?.ShowAIScanBox == true &&
        ((CurrentState == SnipState.Detecting && CurrentMode != SnipMode.Translation)
         || (CurrentMode == SnipMode.Translation && ShowOcrResult));

    /// <summary>
    /// Win32 window/control snap candidate boxes. Always false in translation mode.
    /// </summary>
    public bool IsWindowSnapCandidateLayerVisible =>
        CurrentState == SnipState.Detecting && CurrentMode != SnipMode.Translation;

    public ObservableCollection<VisualRect> WindowRects { get; } = new();
    public ObservableCollection<VisualRect> AIScanRects { get; } = new();
    public ObservableCollection<VisualRect> TranslationOcrSearchRects { get; } = new();
    private readonly IWindowDetectionService _detectionService;

    private Rect _animatedHoverRect;
    public Rect AnimatedHoverRect
    {
        get => _animatedHoverRect;
        private set
        {
            this.RaiseAndSetIfChanged(ref _animatedHoverRect, value);
            this.RaisePropertyChanged(nameof(IsHoverPreviewVisible));
        }
    }

    private double _hoverDashOffset;
    public double HoverDashOffset
    {
        get => _hoverDashOffset;
        private set => this.RaiseAndSetIfChanged(ref _hoverDashOffset, value);
    }

    public bool IsHoverPreviewVisible =>
        CurrentMode != SnipMode.Translation
        && CurrentState == SnipState.Detecting
        && !IsDrawingMode
        && RecState == RecordingState.Idle
        && AnimatedHoverRect.Width > 0
        && AnimatedHoverRect.Height > 0;

    public bool IsTranslationOcrSearchActive
    {
        get => _isTranslationOcrSearchActive;
        private set => this.RaiseAndSetIfChanged(ref _isTranslationOcrSearchActive, value);
    }

    private void InitializeWindowSnapHoverAnimation()
    {
        _hoverAnimationTimer.Tick += (_, _) => AdvanceWindowSnapHoverAnimation();
    }

    /// <summary>
     /// Clears screenshot/recording AI candidate boxes and hover preview. Not used in translation mode.
     /// </summary>
    private void ClearAiScanOverlayState()
    {
        AIScanRects.Clear();
        _ocrRawRects = [];
        _ocrLineCandidates = [];
        _ocrParagraphCandidates = [];
        DismissWindowSnapHoverPreview(preserveTargetRect: false);
        _scanCts?.Cancel();
    }

    private void ClearTranslationOcrSearchOverlay()
    {
        TranslationOcrSearchRects.Clear();
        _currentHoverTranslationOcrCandidate = null;
        IsTranslationOcrSearchActive = false;
    }

    private void InvalidateTranslationOcrSearchCache()
    {
        _translationOcrCacheValid = false;
        _translationOcrRawRects = [];
        _translationOcrLineCandidates = [];
        _translationOcrParagraphCandidates = [];
        _currentHoverTranslationOcrCandidate = null;
        TranslationOcrSearchRects.Clear();
    }

    private void RefreshTranslationOcrSearchRects()
    {
        TranslationOcrSearchRects.Clear();

        if (!IsTranslationOcrSearchActive || CurrentMode != SnipMode.Translation)
        {
            return;
        }

        foreach (var candidate in _translationOcrLineCandidates)
        {
            TranslationOcrSearchRects.Add(new VisualRect(candidate.Bounds));
        }
    }

    private bool IsTranslationOcrCacheCurrent()
    {
        if (!_translationOcrCacheValid)
        {
            return false;
        }

        return _translationOcrCacheViewportRect == new Rect(0, 0, ViewportSize.Width, ViewportSize.Height)
            && _translationOcrCacheScreenOffset == ScreenOffset
            && Math.Abs(_translationOcrCacheVisualScaling - VisualScaling) < 0.0001;
    }

    private async Task EnsureTranslationOcrSearchCandidatesAsync(bool forceRefresh = false)
    {
        if (CurrentMode != SnipMode.Translation || _mainVm == null || _translationSession == null)
        {
            return;
        }

        if (!forceRefresh && IsTranslationOcrCacheCurrent())
        {
            RefreshTranslationOcrSearchRects();
            return;
        }

        if (_isTranslationOcrScanRunning)
        {
            return;
        }

        _isTranslationOcrScanRunning = true;
        ShowTopLoadingBar = true;
        IsIndeterminate = true;

        try
        {
            if (!await EnsureTranslationEngineReadyAsync())
            {
                return;
            }

            bool ready = await _translationSession.EnsureOcrReadyAsync(_mainVm.SourceLanguage);
            if (!ready)
            {
                return;
            }

            var fullScreenRect = new Rect(0, 0, ViewportSize.Width, ViewportSize.Height);
            using var bitmap = await _captureService.CaptureScreenAsync(fullScreenRect, ScreenOffset, VisualScaling, false);
            if (bitmap == null)
            {
                return;
            }

            using var resizedBitmap = CreateOptimizedTranslationBitmap(
                bitmap,
                VisualScaling,
                TranslationSearchCaptureMaxLongSide,
                out _);
            var detectionBitmap = resizedBitmap ?? bitmap;
            LogTranslationMemoryState("translation-ocr-search-capture", bitmap: detectionBitmap);

            using var ocrEngine = new PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.OcrRuntimeService);
            await ocrEngine.EnsureLoadedAsync(_mainVm.AppSettingsService.Settings.SourceLanguage);
            var textBoxes = await Task.Run(() => ocrEngine.DetectText(detectionBitmap));

            double scaleX = ViewportSize.Width / detectionBitmap.Width;
            double scaleY = ViewportSize.Height / detectionBitmap.Height;
            var rawLogicalRects = new List<Rect>(textBoxes.Count);
            var toolbarRect = GetTranslationToolbarRect();

            foreach (var box in textBoxes)
            {
                var bounds = new Rect(
                    box.Left * scaleX,
                    box.Top * scaleY,
                    box.Width * scaleX,
                    box.Height * scaleY);

                if (bounds.Width <= 10 || bounds.Height <= 5)
                {
                    continue;
                }

                if (toolbarRect.HasValue && bounds.Intersects(toolbarRect.Value))
                {
                    continue;
                }

                rawLogicalRects.Add(bounds);
            }

            var grouped = OcrCandidateGrouper.Group(rawLogicalRects);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _translationOcrRawRects = grouped.RawRects;
                _translationOcrLineCandidates = grouped.LineCandidates;
                _translationOcrParagraphCandidates = grouped.ParagraphCandidates;
                _translationOcrCacheViewportRect = fullScreenRect;
                _translationOcrCacheScreenOffset = ScreenOffset;
                _translationOcrCacheVisualScaling = VisualScaling;
                _translationOcrCacheValid = true;
                RefreshTranslationOcrSearchRects();
            });
        }
        finally
        {
            _isTranslationOcrScanRunning = false;
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
        }
    }

    private Rect? GetTranslationToolbarRect()
    {
        if (ToolbarWidth <= 0 || ToolbarHeight <= 0)
        {
            return null;
        }

        double tbLeft = TranslationToolbarLeft >= 0
            ? TranslationToolbarLeft
            : (ViewportSize.Width - (ToolbarWidth > 0 ? ToolbarWidth : 200)) / 2;
        double tbTop = TranslationToolbarTop;
        double tbWidth = ToolbarWidth > 0 ? ToolbarWidth + 20 : 220;
        double tbHeight = ToolbarHeight > 0 ? ToolbarHeight + 10 : 55;
        return new Rect(tbLeft, tbTop, tbWidth, tbHeight);
    }

    internal bool TryMaterializeTranslationOcrCandidateAtPoint(Point point)
    {
        if (CurrentMode != SnipMode.Translation || (_translationOcrParagraphCandidates.Count == 0 && _translationOcrLineCandidates.Count == 0))
        {
            return false;
        }

        var candidate = OcrCandidateHitTester.SelectTranslationCandidate(
            point,
            _translationOcrParagraphCandidates,
            _translationOcrLineCandidates,
            _currentHoverTranslationOcrCandidate);
        if (candidate == null)
        {
            _currentHoverTranslationOcrCandidate = null;
            return false;
        }

        _currentHoverTranslationOcrCandidate = candidate;
        return TryMaterializeTranslationOcrCandidate(candidate);
    }

    private bool TryMaterializeTranslationOcrCandidate(OcrCandidate candidate)
    {
        Rect candidateBounds = ResolveTranslationCandidateBounds(candidate);

        foreach (var existing in UserSelections)
        {
            if (existing.IsAudioPanel)
            {
                continue;
            }

            if (Math.Abs(existing.Bounds.X - candidateBounds.X) < 0.5
                && Math.Abs(existing.Bounds.Y - candidateBounds.Y) < 0.5
                && Math.Abs(existing.Bounds.Width - candidateBounds.Width) < 0.5
                && Math.Abs(existing.Bounds.Height - candidateBounds.Height) < 0.5)
            {
                return false;
            }
        }

        UserSelections.Add(new UserSelectionRect { Bounds = candidateBounds });
        RefreshInteractionRegion();
        return true;
    }

    private Rect ResolveTranslationCandidateBounds(OcrCandidate candidate)
    {
        if (candidate.Kind != OcrCandidateKind.Line)
        {
            return candidate.Bounds;
        }

        var siblingLines = _translationOcrLineCandidates.AsValueEnumerable()
            .Where(line => line.GroupId == candidate.GroupId)
            .OrderBy(line => line.Bounds.Y)
            .ToList();

        int selectedIndex = siblingLines.FindIndex(line => line.IsSameIdentity(candidate));
        if (selectedIndex < 0)
        {
            return candidate.Bounds;
        }

        Rect combinedBounds = siblingLines[selectedIndex].Bounds;

        for (int i = selectedIndex - 1; i >= 0; i--)
        {
            if (!CanJoinLocalTranslationCluster(siblingLines[i], combinedBounds))
            {
                break;
            }

            combinedBounds = combinedBounds.Union(siblingLines[i].Bounds);
        }

        for (int i = selectedIndex + 1; i < siblingLines.Count; i++)
        {
            if (!CanJoinLocalTranslationCluster(siblingLines[i], combinedBounds))
            {
                break;
            }

            combinedBounds = combinedBounds.Union(siblingLines[i].Bounds);
        }

        return combinedBounds;
    }

    private static bool CanJoinLocalTranslationCluster(OcrCandidate candidate, Rect currentBounds)
    {
        double verticalGap = candidate.Bounds.Top >= currentBounds.Bottom
            ? candidate.Bounds.Top - currentBounds.Bottom
            : currentBounds.Top >= candidate.Bounds.Bottom
                ? currentBounds.Top - candidate.Bounds.Bottom
                : 0;

        double maxHeight = Math.Max(candidate.Bounds.Height, currentBounds.Height);
        if (verticalGap > Math.Max(maxHeight * 0.55, 10.0))
        {
            return false;
        }

        double overlapWidth = Math.Max(
            0,
            Math.Min(candidate.Bounds.Right, currentBounds.Right) - Math.Max(candidate.Bounds.Left, currentBounds.Left));
        double minWidth = Math.Min(candidate.Bounds.Width, currentBounds.Width);
        if (minWidth <= 0)
        {
            return false;
        }

        return (overlapWidth / minWidth) >= 0.5;
    }

    private void MaterializeAllTranslationOcrCandidates()
    {
        if (_translationOcrParagraphCandidates.Count == 0)
        {
            return;
        }

        UserSelections.Clear();

        foreach (var candidate in _translationOcrParagraphCandidates)
        {
            UserSelections.Add(new UserSelectionRect { Bounds = candidate.Bounds });
        }

        if (CurrentTranslationTool == TranslationTool.Audio)
        {
            EnsureAudioTranslationBox();
        }

        RefreshInteractionRegion();
    }

    public async Task EnterTranslationOcrSearchAsync()
    {
        if (CurrentMode != SnipMode.Translation)
        {
            return;
        }

        // Ctrl key-repeat and the Avalonia/Win32 keyboard paths may report the same
        // press more than once. Re-entering used to clear the overlay and force a
        // new scan each time, which appeared as continuous flickering.
        if (IsTranslationOcrSearchActive)
        {
            System.Diagnostics.Debug.WriteLine("[TranslationOCR] Ignored duplicate OCR search entry.");
            return;
        }

        InvalidateTranslationOcrSearchCache();
        IsTranslationOcrSearchActive = true;
        RefreshTranslationOcrSearchRects();
        await EnsureTranslationOcrSearchCandidatesAsync(forceRefresh: true);
    }

    public void ExitTranslationOcrSearch()
    {
        ClearTranslationOcrSearchOverlay();
        InvalidateTranslationOcrSearchCache();
    }

    public void RefreshWindowRects(IntPtr? excludeHWnd = null)
    {
        if (CurrentMode == SnipMode.Translation)
        {
            return;
        }

        var globalCandidates = _detectionService.GetVisibleWindowCandidates(excludeHWnd);
        _windowCandidates = globalCandidates.AsValueEnumerable()
            .Select(candidate => ToLocalCandidate(candidate))
            .ToList();

        WindowRects.Clear();
        foreach (var rect in _windowCandidates.AsValueEnumerable().Where(candidate => candidate.Kind == WindowCandidateKind.TopLevel))
        {
            WindowRects.Add(new VisualRect(rect.Bounds));
        }

        DismissWindowSnapHoverPreview(preserveTargetRect: false);
    }

    public void UpdateWindowHover(Point mousePos)
    {
        if (CurrentState != SnipState.Detecting || CurrentMode == SnipMode.Translation || IsDrawingMode || RecState != RecordingState.Idle)
        {
            DismissWindowSnapHoverPreview(preserveTargetRect: false);
            return;
        }

        var windowCandidate = _detectionService.GetCandidateAtPoint(mousePos, _windowCandidates, _currentHoverCandidate);
        var ocrCandidate = OcrCandidateHitTester.SelectCandidate(mousePos, _ocrParagraphCandidates, _ocrLineCandidates, _currentHoverOcrCandidate);
        var selected = SelectBestHoverCandidate(windowCandidate, ocrCandidate);
        if (selected == null)
        {
            _currentHoverCandidate = null;
            _currentHoverOcrCandidate = null;
            HoverTargetRect = default;
            DismissWindowSnapHoverPreview(preserveTargetRect: false);
            return;
        }

        HoverTargetRect = selected.Bounds;

        bool targetChanged = !string.Equals(_currentHoverKey, selected.Key, StringComparison.Ordinal);
        _currentHoverKey = selected.Key;
        _currentHoverCandidate = selected.WindowCandidate;
        _currentHoverOcrCandidate = selected.OcrCandidate;

        if (targetChanged)
        {
            _hoverAnimationController.SetTarget(selected.Bounds);
            ApplyWindowSnapHoverAnimationState();
        }

        EnsureWindowSnapHoverTimerRunning();
    }

    private void AdvanceWindowSnapHoverAnimation()
    {
        if (!_hoverAnimationTimer.IsEnabled)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (_lastHoverAnimationTickUtc == default)
        {
            _lastHoverAnimationTickUtc = now;
            return;
        }

        var elapsed = now - _lastHoverAnimationTickUtc;
        _lastHoverAnimationTickUtc = now;
        _hoverAnimationController.Advance(elapsed);
        ApplyWindowSnapHoverAnimationState();

        if (!ShouldKeepWindowSnapHoverTimerRunning())
        {
            _hoverAnimationTimer.Stop();
        }
    }

    private void EnsureWindowSnapHoverTimerRunning()
    {
        if (!ShouldKeepWindowSnapHoverTimerRunning())
        {
            return;
        }

        if (!_hoverAnimationTimer.IsEnabled)
        {
            _lastHoverAnimationTickUtc = DateTime.UtcNow;
            _hoverAnimationTimer.Start();
        }
        else if (_lastHoverAnimationTickUtc == default)
        {
            _lastHoverAnimationTickUtc = DateTime.UtcNow;
        }
    }

    private bool ShouldKeepWindowSnapHoverTimerRunning()
    {
        return _hoverAnimationController.IsVisible
            && CurrentState == SnipState.Detecting
            && CurrentMode != SnipMode.Translation
            && !IsDrawingMode
            && RecState == RecordingState.Idle;
    }

    private void ApplyWindowSnapHoverAnimationState()
    {
        AnimatedHoverRect = _hoverAnimationController.IsVisible ? _hoverAnimationController.CurrentRect : default;
        HoverDashOffset = _hoverAnimationController.DashOffset;
    }

    private void DismissWindowSnapHoverPreview(bool preserveTargetRect)
    {
        _hoverAnimationTimer.Stop();
        _hoverAnimationController.Hide();
        _lastHoverAnimationTickUtc = default;
        AnimatedHoverRect = default;
        HoverDashOffset = 0;

        if (!preserveTargetRect)
        {
            HoverTargetRect = default;
            _currentHoverCandidate = null;
            _currentHoverOcrCandidate = null;
            _currentHoverKey = null;
        }
    }

    private HoverSelection? SelectBestHoverCandidate(WindowCandidate? windowCandidate, OcrCandidate? ocrCandidate)
    {
        if (windowCandidate == null && ocrCandidate == null)
        {
            return null;
        }

        if (ocrCandidate == null)
        {
            return HoverSelection.FromWindow(windowCandidate!);
        }

        if (windowCandidate == null)
        {
            return HoverSelection.FromOcr(ocrCandidate);
        }

        if (ShouldPreferOcrCandidate(ocrCandidate, windowCandidate))
        {
            return HoverSelection.FromOcr(ocrCandidate);
        }

        return HoverSelection.FromWindow(windowCandidate);
    }

    private static bool ShouldPreferOcrCandidate(OcrCandidate ocrCandidate, WindowCandidate windowCandidate)
    {
        if (ocrCandidate.Kind == OcrCandidateKind.Line)
        {
            return true;
        }

        return ocrCandidate.Area < (windowCandidate.Area * 0.75);
    }

    private void RefreshProjectedOcrRects()
    {
        AIScanRects.Clear();

        if (_mainVm?.ShowAIScanBox != true)
        {
            return;
        }

        IReadOnlyList<Rect> sourceRects = ShowOcrResult
            ? _ocrRawRects
            : _ocrParagraphCandidates.AsValueEnumerable().Select(candidate => candidate.Bounds).ToList();

        foreach (var rect in sourceRects)
        {
            AIScanRects.Add(new VisualRect(rect));
        }
    }

    private sealed class HoverSelection
    {
        private HoverSelection(Rect bounds, string key, WindowCandidate? windowCandidate, OcrCandidate? ocrCandidate)
        {
            Bounds = bounds;
            Key = key;
            WindowCandidate = windowCandidate;
            OcrCandidate = ocrCandidate;
        }

        public Rect Bounds { get; }
        public string Key { get; }
        public WindowCandidate? WindowCandidate { get; }
        public OcrCandidate? OcrCandidate { get; }

        public static HoverSelection FromWindow(WindowCandidate candidate)
            => new(candidate.Bounds, $"W:{candidate.Hwnd}:{candidate.Kind}:{candidate.ZOrder}", candidate, null);

        public static HoverSelection FromOcr(OcrCandidate candidate)
            => new(candidate.Bounds, $"O:{candidate.Kind}:{candidate.GroupId}:{candidate.Bounds.X:F1}:{candidate.Bounds.Y:F1}", null, candidate);
    }

    private WindowCandidate ToLocalCandidate(WindowCandidate candidate)
    {
        Rect bounds = candidate.Bounds;
        Rect localBounds = new(
            (bounds.X - ScreenOffset.X) / VisualScaling,
            (bounds.Y - ScreenOffset.Y) / VisualScaling,
            bounds.Width / VisualScaling,
            bounds.Height / VisualScaling);

        return new WindowCandidate(
            localBounds,
            candidate.Hwnd,
            candidate.ParentHwnd,
            candidate.RootHwnd,
            candidate.ZOrder,
            candidate.Kind);
    }

    private System.Threading.CancellationTokenSource? _scanCts;

    private async Task RunAIScanAsync()
    {
        if (CurrentMode == SnipMode.Translation) return;

        await RunOCRScanAsync();
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

        var scanCts = new System.Threading.CancellationTokenSource();
        var previousScanCts = System.Threading.Interlocked.Exchange(ref _scanCts, scanCts);
        previousScanCts?.Cancel();
        var token = scanCts.Token;

        ShowTopLoadingBar = true;

        try
        {
            if (CurrentState == SnipState.Detecting)
                ProcessingText = LocalizationService.Instance["StatusAIScanning"] ?? "AI Scanning...";

            var progress = new Progress<AIScanStage>(_ =>
            {
                if (CurrentState == SnipState.Detecting)
                {
                    ProcessingText = LocalizationService.Instance["StatusAIScanning"] ?? "AI Scanning...";
                }
            });

            var result = await _aiScanSessionService.RunScanAsync(CreateAIScanSessionRequest(), progress, token);
            if (!result.IsReady)
            {
                System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] OCR resources are not ready");
                return;
            }

            ApplyOcrScanResult(result, token);
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
            System.Threading.Interlocked.CompareExchange(ref _scanCts, null, scanCts);
            scanCts.Dispose();
        }
    }

    private AIScanSessionRequest CreateAIScanSessionRequest()
    {
        return new AIScanSessionRequest(
            new Rect(0, 0, ViewportSize.Width, ViewportSize.Height),
            ScreenOffset,
            VisualScaling,
            _mainVm?.AppSettingsService.Settings.SourceLanguage ?? OCRLanguage.TraditionalChinese);
    }

    private void ApplyOcrScanResult(AIScanSessionResult result, System.Threading.CancellationToken token)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            if (CurrentState != SnipState.Detecting || _mainVm?.EnableAI != true || CurrentMode == SnipMode.Translation) return;

            _ocrRawRects = result.RawDetectedRects.AsValueEnumerable().ToList();
            _ocrParagraphCandidates = result.Candidates.AsValueEnumerable()
                .Where(candidate => candidate.Kind == OcrCandidateKind.Paragraph)
                .ToList();
            _ocrLineCandidates = result.Candidates.AsValueEnumerable()
                .Where(candidate => candidate.Kind == OcrCandidateKind.Line)
                .ToList();
            RefreshProjectedOcrRects();
        });
    }

    private Rect _activeScreenBounds = new Rect(0,0,1920,1080); // Default
    public Rect ActiveScreenBounds
    {
        get => _activeScreenBounds;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeScreenBounds, value);
            this.RaisePropertyChanged(nameof(ToolbarMaxWidth));
            if (CurrentMode == SnipMode.Translation && !IsToolbarManuallyPositioned)
            {
                InitializeTranslationToolbarPosition();
            }
        }
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

        TriggerAutoScanCommand = ReactiveCommand.CreateFromTask(RunOCRScanAsync);
        TriggerAutoScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Auto OCR Scan Command error: {ex}"));

        ToggleAIScanBoxCommand = ReactiveCommand.Create(() => { ShowAIScanBox = !ShowAIScanBox; return Unit.Default; });
        ToggleAIScanBoxCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Toggle AI Box error: {ex}"));


        var canExecuteInTranslation = this.WhenAnyValue(
            x => x.CurrentMode,
            x => x.IsInputFocused,
            (mode, textFocus) => mode == SnipMode.Translation && !textFocus);

        TranslateAllSelectionsCommand = ReactiveCommand.Create(
            () => TranslateAllSelectionsAsync().Forget("Translation.TranslateAllSelections"),
            canExecuteInTranslation);
        TranslateAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"TranslateAll error: {ex}"));

        ScanAllTextCommand = ReactiveCommand.CreateFromTask(ScanAllDetectedTranslationParagraphsAsync, canExecuteInTranslation);
        ScanAllTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ScanAll error: {ex}"));

        ClearAllSelectionsCommand = ReactiveCommand.Create(() =>
        {
            UserSelections.Clear();
            InvalidateTranslationOcrSearchCache();
            TranslationResultLayerManager.ClearAll();
            if (CurrentTranslationTool == TranslationTool.Audio)
            {
                EnsureAudioTranslationBox();
            }
            RefreshInteractionRegion();
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
                TranslationResultItem result => !string.IsNullOrWhiteSpace(result.TranslatedText)
                    ? result.TranslatedText
                    : result.OriginalText,
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

        // Keep native interaction regions and audio helper state in sync with selection edits.
        UserSelections.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (UserSelectionRect item in e.NewItems)
                {
                    item.WhenAnyValue(x => x.Bounds, x => x.IsTranslated)
                        .Subscribe(_ => RefreshInteractionRegion());
                }
            }

            // Keep one persistent audio panel while in Audio mode.
            if (CurrentTranslationTool == TranslationTool.Audio && !UserSelections.AsValueEnumerable().Any(x => x.IsAudioPanel))
            {
                EnsureAudioTranslationBox();
            }

            RefreshInteractionRegion();
        };
    }
}
