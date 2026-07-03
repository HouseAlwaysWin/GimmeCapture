using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Rendering;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

// Pin-parity editing composed from the shared components: annotations (AnnotationEditorState via
// DrawingToolAdapter), redaction (RedactionEditorState), the top toolbar categories, and the playback
// extras (mute / loop / global speed via AudioPreviewPlayer). Annotations and redaction live in SURFACE
// space — the cropped+rotated preview-frame pixel size — and are burned post-transform at encode.
internal sealed partial class VideoEditViewModel
{
    // ── Annotations (shared state + IDrawingToolViewModel adapter for DrawingToolbar/TextEntryOverlay) ──
    public AnnotationEditorState EditorState { get; } = new();

    /// <summary>DataContext for the shared drawing controls.</summary>
    public DrawingToolAdapter Draw { get; private set; } = null!;

    // ── Inline quality compare (hosted in the editor preview; replaces the old separate CompareWindow) ──
    private bool _isComparing;
    /// <summary>True while the inline before/after quality comparison is showing. Drives the panel visibility
    /// and locks all editing controls (bound as <c>!IsComparing</c>) until the compare view is closed.</summary>
    public bool IsComparing { get => _isComparing; private set => this.RaiseAndSetIfChanged(ref _isComparing, value); }

    private CompareViewModel? _compare;
    /// <summary>The hosted compare engine (encode sample + decode source/sample frames); null when not comparing.</summary>
    public CompareViewModel? Compare { get => _compare; private set => this.RaiseAndSetIfChanged(ref _compare, value); }

    // 畫質比較 button is a toggle: open the inline compare, or close it if it's already showing.
    private async Task ToggleCompareAsync()
    {
        if (IsComparing)
        {
            CloseCompare();
            return;
        }

        await StartCompareAsync();
    }

    // Compare button: build the engine for the current playhead, show it inline, then load (encode) on click.
    private async Task StartCompareAsync()
    {
        if (IsComparing || BuildCompareViewModel == null)
        {
            return;
        }

        CompareViewModel? vm = BuildCompareViewModel(PositionSeconds);
        if (vm == null)
        {
            return;
        }

        Pause(); // stop the editor's own playback while comparing
        Compare = vm;
        IsComparing = true;
        await vm.InitializeAsync(); // encodes the sample (shows its IsPreparing overlay), then the first frame
    }

    // Close button in the inline compare panel: tear the engine down and re-enable editing.
    private void CloseCompare()
    {
        if (!IsComparing)
        {
            return;
        }

        IsComparing = false;
        CompareViewModel? old = Compare;
        Compare = null;
        old?.Dispose(); // stop playback + delete the temp sample
    }

    // ── Redaction ──
    public RedactionEditorState Redaction { get; private set; } = null!;

    private Avalonia.Rect _selectionRect;
    /// <summary>Marquee drawn on the preview while in selection mode (redaction keyframe source).</summary>
    public Avalonia.Rect SelectionRect
    {
        get => _selectionRect;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            this.RaisePropertyChanged(nameof(IsSelectionActive));
        }
    }

    public bool IsSelectionActive => _selectionRect.Width > 0 && _selectionRect.Height > 0;

    private bool _isSelectionMode;
    /// <summary>When on, dragging on the preview draws the selection marquee instead of annotations.</summary>
    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelectionMode, value);
            if (!value)
            {
                SelectionRect = new Avalonia.Rect();
            }
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleSelectionModeCommand { get; private set; } = null!;

    // ── Toolbar categories (mirrors FloatingWindowViewModelBase's toggle-collapse) ──
    private ToolbarCategory _activeToolbarCategory = ToolbarCategory.Edit;
    public ToolbarCategory ActiveToolbarCategory
    {
        get => _activeToolbarCategory;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeToolbarCategory, value);
            this.RaisePropertyChanged(nameof(IsAnnotateCategory));
            this.RaisePropertyChanged(nameof(IsEditCategory));
            this.RaisePropertyChanged(nameof(IsRedactCategory));
            this.RaisePropertyChanged(nameof(IsSubToolbarVisible));
        }
    }

    public bool IsAnnotateCategory => _activeToolbarCategory == ToolbarCategory.Annotate;
    public bool IsEditCategory => _activeToolbarCategory == ToolbarCategory.Edit;
    public bool IsRedactCategory => _activeToolbarCategory == ToolbarCategory.Redact;
    public bool IsSubToolbarVisible => _activeToolbarCategory != ToolbarCategory.None;

    public ReactiveCommand<ToolbarCategory, Unit> SelectToolbarCategoryCommand { get; private set; } = null!;

    // ── Playback extras (mute / loop / global speed) ──
    private readonly AudioPreviewPlayer _audioPreview = new();

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isMuted, value);
            UpdateAudioForPlayback();
        }
    }

    private bool _isLooping;
    public bool IsLooping { get => _isLooping; set => this.RaiseAndSetIfChanged(ref _isLooping, value); }

    private double _previewVolume = 1.0;
    /// <summary>Preview playback volume (0–1). Preview-only — does not change the encoded output.</summary>
    public double PreviewVolume
    {
        get => _previewVolume;
        set
        {
            this.RaiseAndSetIfChanged(ref _previewVolume, value);
            _audioPreview.Volume = (float)Math.Clamp(value, 0.0, 1.0);
        }
    }

    private double _playbackSpeed = 1.0;
    /// <summary>Global preview speed (0.5/1/1.5/2×), multiplied with each kept run's own speed.</summary>
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        private set
        {
            this.RaiseAndSetIfChanged(ref _playbackSpeed, value);
            this.RaisePropertyChanged(nameof(PlaybackSpeedText));
        }
    }

    public string PlaybackSpeedText => $"{_playbackSpeed:0.#}×";

    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleLoopCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CycleGlobalSpeedCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> FreezeFrameCommand { get; private set; } = null!;

    /// <summary>Set by the view: hands a flattened (frame + annotations) bitmap to a new image pin.</summary>
    public Action<Bitmap>? FreezeFrameToImagePinAction { get; set; }

    /// <summary>
    /// Set by the view: confirm that changing crop/rotation clears existing annotations/redaction.
    /// Null (headless/tests) auto-confirms.
    /// </summary>
    public Func<Task<bool>>? ConfirmTransformClearsEditsAction { get; set; }

    // Wire the composed editing pieces; called once from the constructor.
    private void InitializeEditing(VideoEditResult initial)
    {
        Draw = new DrawingToolAdapter(EditorState);

        Redaction = new RedactionEditorState(
            () => (SurfaceWidth, SurfaceHeight),
            () => PositionSeconds,
            () => SelectionRect,
            () => SelectionRect = new Avalonia.Rect(),
            this.WhenAnyValue(x => x.IsSelectionActive));

        foreach (Annotation ann in initial.Annotations)
        {
            EditorState.Annotations.Add(ann);
        }
        if (initial.RedactionTracks.Count > 0)
        {
            Redaction.LoadTracks(initial.RedactionTracks);
        }

        SelectToolbarCategoryCommand = ReactiveCommand.Create<ToolbarCategory>(category =>
        {
            ActiveToolbarCategory = ActiveToolbarCategory == category ? ToolbarCategory.None : category;
            if (!IsRedactCategory)
            {
                IsSelectionMode = false; // leaving the redact tools clears the marquee mode
            }
            if (!IsAnnotateCategory)
            {
                EditorState.CurrentAnnotationTool = AnnotationType.None;
            }
        });

        ToggleSelectionModeCommand = ReactiveCommand.Create(() =>
        {
            IsSelectionMode = !IsSelectionMode;
            if (IsSelectionMode)
            {
                EditorState.CurrentAnnotationTool = AnnotationType.None;
            }
        });

        ToggleMuteCommand = ReactiveCommand.Create(() => { IsMuted = !IsMuted; });
        ToggleLoopCommand = ReactiveCommand.Create(() => { IsLooping = !IsLooping; });
        CycleGlobalSpeedCommand = ReactiveCommand.Create(() =>
        {
            PlaybackSpeed = PlaybackSpeed switch
            {
                0.5 => 1.0,
                1.0 => 1.5,
                1.5 => 2.0,
                _ => 0.5,
            };
            if (IsPlaying)
            {
                _ = PlayAsync(); // re-capture the new effective speed (video + audio)
            }
        });
        FreezeFrameCommand = ReactiveCommand.CreateFromTask(FreezeFrameAsync);
        FreezeFrameCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.EditFreeze", ex));

        // The live redaction boxes follow the playhead (scrub + playback). Throttled without a
        // time-based Rx operator (those hang on the immediate test scheduler).
        long lastBoxRefreshMs = 0;
        this.WhenAnyValue(x => x.PositionSeconds).Subscribe(_ =>
        {
            if (Redaction.RedactionTracks.Count == 0)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now - lastBoxRefreshMs < 66)
            {
                return;
            }

            lastBoxRefreshMs = now;
            Redaction.RefreshActiveBoxes();
        });
    }

    // ── Surface space: the cropped+rotated preview-frame pixel size annotations/redaction are stored in ──

    /// <summary>Decode-space crop rect (same math as the preview's CropSkBitmap; null = no crop).</summary>
    private (int X, int Y, int W, int H)? DecodeCropRect()
    {
        if (_crop == null)
        {
            return null;
        }

        double sx = _decodeW / (double)_sourceWidth;
        double sy = _decodeH / (double)_sourceHeight;
        int x = Math.Clamp((int)Math.Round(_crop.X * sx), 0, _decodeW - 2);
        int y = Math.Clamp((int)Math.Round(_crop.Y * sy), 0, _decodeH - 2);
        int w = Math.Clamp((int)Math.Round(_crop.Width * sx), 2, _decodeW - x);
        int h = Math.Clamp((int)Math.Round(_crop.Height * sy), 2, _decodeH - y);
        return (x, y, w, h);
    }

    public double SurfaceWidth
    {
        get
        {
            (int w, int h) = SurfaceBaseDims();
            return _rotation is 90 or 270 ? h : w;
        }
    }

    public double SurfaceHeight
    {
        get
        {
            (int w, int h) = SurfaceBaseDims();
            return _rotation is 90 or 270 ? w : h;
        }
    }

    private (int W, int H) SurfaceBaseDims()
    {
        var crop = DecodeCropRect();
        return crop is { } c ? (c.W, c.H) : (_decodeW, _decodeH);
    }

    private void RaiseSurfaceChanged()
    {
        this.RaisePropertyChanged(nameof(SurfaceWidth));
        this.RaisePropertyChanged(nameof(SurfaceHeight));
        SegmentLayoutChanged?.Invoke(); // the window re-aligns the overlay stack with the new aspect
    }

    // Changing crop/rotation invalidates surface-space annotation/redaction coordinates → confirm + clear.
    private bool HasBurnInEdits => EditorState.Annotations.Count > 0 || Redaction.HasRedaction;

    private async Task<bool> ConfirmTransformChangeAsync()
    {
        if (!HasBurnInEdits)
        {
            return true;
        }

        bool ok = ConfirmTransformClearsEditsAction == null || await ConfirmTransformClearsEditsAction();
        if (ok)
        {
            EditorState.ClearAnnotations();
            Redaction.Clear();
        }

        return ok;
    }

    // ── Freeze frame: flatten the current (cropped+rotated) frame + annotations into an image pin ──
    private async Task FreezeFrameAsync()
    {
        if (FreezeFrameToImagePinAction == null)
        {
            return;
        }

        Pause();
        await Task.Yield();

        Bitmap? frame = Frame;
        if (frame == null)
        {
            return;
        }

        try
        {
            if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(frame, out SKBitmap? sk, out _) || sk == null)
            {
                return;
            }

            using (sk)
            {
                if (EditorState.Annotations.Count > 0)
                {
                    AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                        sk, EditorState.Annotations.ToList(), SurfaceWidth, SurfaceHeight, sk.Width, sk.Height);
                }

                if (FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(sk, out Bitmap? flat, out _) && flat != null)
                {
                    FreezeFrameToImagePinAction.Invoke(flat);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.EditFreezeFlatten", ex);
        }
    }

    // Start/refresh preview audio to match the current playback state (called from the video loop).
    private void UpdateAudioForPlayback()
    {
        if (!IsPlaying || IsMuted)
        {
            _audioPreview.Stop();
            return;
        }

        double pieceSpeed = 1.0;
        VideoEditSegment[] runs = KeptRuns();
        int idx = -1;
        for (int i = 0; i < runs.Length; i++)
        {
            if (runs[i].SourceEnd > PositionSeconds + 0.0005)
            {
                idx = i;
                break;
            }
        }
        if (idx >= 0 && runs[idx].Speed > 0)
        {
            pieceSpeed = runs[idx].Speed;
        }

        _audioPreview.Start(_sourcePath, Math.Max(0, PositionSeconds), _playbackSpeed * pieceSpeed);
    }

    private void StopAudioPreview() => _audioPreview.Stop();
}
