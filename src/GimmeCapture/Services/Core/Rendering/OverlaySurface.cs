using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Rendering;

/// <summary>
/// Owns the snip overlay's Live/Frozen state machine: the transitions, the frozen still's lifetime, and every
/// fact derived from that state.
///
/// The freeze decision used to be a boolean each caller re-read and re-interpreted, which is why it drifted:
/// one capture action forgot to branch and uploaded live pixels from a frozen overlay, a mode switch left the
/// overlay painting a stale still, and an unfreeze could dispose a bitmap out from under a running OCR scan.
/// Those are state-ownership failures, not pixel-plumbing failures, so the seam sits at "what state is this
/// overlay in, and what does that imply" rather than at "give me the selection's pixels".
///
/// THREADING — transitions build an Avalonia bitmap and raise change notifications, so call them on the UI
/// thread. <see cref="CommitAsync"/> may be awaited from the UI thread (the pixel work runs off it).
/// <see cref="LeaseFrozenStill"/> and lease disposal are thread-safe: a background OCR thread returns its lease.
/// </summary>
public sealed class OverlaySurface : ReactiveObject, IDisposable
{
    private readonly IScreenCaptureService _capture;
    private readonly ICaptureVisibilityCoordinator _visibility;
    private readonly Func<SelectionCommit> _commitProvider;
    private Func<SKBitmap, Avalonia.Media.Imaging.Bitmap?>? _toDisplay;

    private readonly object _gate = new();
    private OverlaySurfaceState _state = OverlaySurfaceState.Live.Instance;
    private OverlayActivity _activity = OverlayActivity.Screenshot;

    // Deferred disposal: the state can flip to Live while a background OCR scan still holds the pixels.
    private OverlayBackdrop? _retiring;
    private int _leases;
    private bool _disposed;

    public OverlaySurface(
        IScreenCaptureService capture,
        ICaptureVisibilityCoordinator visibility,
        Func<SelectionCommit> commitProvider,
        Func<SKBitmap, Avalonia.Media.Imaging.Bitmap?>? toDisplay = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _visibility = visibility ?? throw new ArgumentNullException(nameof(visibility));
        _commitProvider = commitProvider ?? throw new ArgumentNullException(nameof(commitProvider));
        _toDisplay = toDisplay ?? OverlayBackdrop.DefaultToDisplay;
    }

    /// <summary>
    /// Test seam: drops the display conversion, so freezing works on a host with no render platform and the
    /// frozen commit path can be asserted at all. Production never calls this — see
    /// <see cref="OverlayBackdrop.TryCreate"/> for why a null converter and a null-returning converter mean
    /// different things.
    /// </summary>
    internal void UseHeadlessBackdropForTest() => _toDisplay = null;

    /// <summary>Hides the overlay window. Assigned by the View; a null action means there is nothing to hide
    /// (the factory's pre-Show phase, design-time, tests).</summary>
    public Action? HideOverlay { get; set; }

    /// <summary>
    /// Runs a grab with the overlay excluded from screen capture, so a mid-session freeze doesn't bake the
    /// overlay's own chrome into the still. Null off-Windows, in design-time, and before the View wires up —
    /// the surface then falls back to a bare grab rather than skipping the grab.
    /// </summary>
    public Func<Func<Task<SKBitmap>>, Task<SKBitmap>>? RunGrabExcludingOverlay { get; set; }

    /// <summary>Never null; starts Live.</summary>
    public OverlaySurfaceState State
    {
        get => _state;
        private set
        {
            if (ReferenceEquals(_state, value)) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsFrozen));
            this.RaisePropertyChanged(nameof(Backdrop));
            this.RaisePropertyChanged(nameof(AllowsNoActivateOverlay));
            this.RaisePropertyChanged(nameof(WantsOpaqueFullHitTest));
        }
    }

    /// <summary>The activity gate. Only <see cref="OverlayActivity.Screenshot"/> may hold a frozen still.</summary>
    public OverlayActivity Activity => _activity;

    // ---- Derived facts. One authority, so the four consumers cannot disagree. ----

    /// <summary>Toolbar toggle active state and backdrop visibility.</summary>
    public bool IsFrozen => _state is OverlaySurfaceState.Frozen;

    /// <summary>The overlay's backdrop Image source; null when live. Owned here — never dispose it.</summary>
    public Avalonia.Media.Imaging.Bitmap? Backdrop => (_state as OverlaySurfaceState.Frozen)?.Backdrop.Image;

    /// <summary>Feeds <c>ShouldAvoidStealingFocus</c>. False when frozen: the still is already grabbed, so
    /// keeping focus away from the overlay no longer buys anything.</summary>
    public bool AllowsNoActivateOverlay => !IsFrozen;

    /// <summary>Win32 pass-through region: true means keep the whole window opaque and fully hit-testable,
    /// because a hole would reveal the live desktop through the frozen picture.</summary>
    public bool WantsOpaqueFullHitTest => IsFrozen;

    // ---- Transitions: the only mutators. ----

    /// <summary>
    /// Pre-overlay freeze (the factory, before Show()). Takes ownership of <paramref name="still"/>
    /// unconditionally: if the backdrop conversion fails or the activity gate refuses, the still is disposed and
    /// the surface stays Live. A partially-frozen state is never published.
    /// </summary>
    /// <returns>The resulting state — Frozen on success, Live on gate or fallback.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="still"/> is null. Unfreezing has its own verb
    /// (<see cref="ReturnToLive"/>); passing null here is not how you unfreeze.</exception>
    public OverlaySurfaceState FreezeFromPreOverlayGrab(SKBitmap still, double grabScaling)
    {
        ArgumentNullException.ThrowIfNull(still);
        return AdoptStill(still, grabScaling, FrozenOrigin.PreOverlay);
    }

    /// <summary>
    /// Mid-session freeze (the toolbar toggle). Grabs the whole viewport through
    /// <see cref="RunGrabExcludingOverlay"/> so the overlay isn't baked into the still and the user sees no
    /// flicker. Idempotent — already frozen returns the current state without a second grab. Never throws for a
    /// failed grab: it logs and returns Live, because a failed freeze must not tear down an open overlay.
    /// </summary>
    public async Task<OverlaySurfaceState> FreezeAsync(OverlayGeometry geometry, CancellationToken ct = default)
    {
        if (_disposed) return _state;
        if (_state is OverlaySurfaceState.Frozen) return _state;
        if (_activity != OverlayActivity.Screenshot || !geometry.IsUsable) return _state;

        double grabScaling = geometry.SafeScaling;
        var grabRegion = new Rect(0, 0, geometry.ViewportSize.Width, geometry.ViewportSize.Height);
        Func<Task<SKBitmap>> grab = () =>
            _capture.CaptureScreenAsync(grabRegion, geometry.ScreenOffset, grabScaling, includeCursor: false);

        SKBitmap still;
        try
        {
            ct.ThrowIfCancellationRequested();
            var excluded = RunGrabExcludingOverlay;
            still = excluded != null ? await excluded(grab) : await grab();
        }
        catch (OperationCanceledException)
        {
            return _state;
        }
        catch (Exception ex)
        {
            AppLog.Warning("OverlaySurface.Freeze", ex);
            return _state;
        }

        return AdoptStill(still, grabScaling, FrozenOrigin.MidSession);
    }

    /// <summary>
    /// Unfreeze. Idempotent. The state flips immediately so the UI is correct at once, but the pixels outlive
    /// the flip until the last outstanding lease is returned.
    /// </summary>
    public OverlaySurfaceState ReturnToLive()
    {
        if (_state is not OverlaySurfaceState.Frozen frozen) return _state;

        RetireBackdrop(frozen.Backdrop);
        State = OverlaySurfaceState.Live.Instance;
        return _state;
    }

    /// <summary>
    /// Record the current activity and enforce the gate: anything other than
    /// <see cref="OverlayActivity.Screenshot"/> forces Live. Idempotent. Call from the mode setter and from
    /// manual-scroll start/finish — this is what stops a frozen still surviving into a recording overlay.
    /// </summary>
    public OverlaySurfaceState ConstrainTo(OverlayActivity activity)
    {
        _activity = activity;
        return activity == OverlayActivity.Screenshot ? _state : ReturnToLive();
    }

    // ---- Commit ----

    /// <summary>
    /// The selection's pixels, with everything the overlay draws over them composited in. The state decides how:
    /// Live hides the overlay and grabs; Frozen hides nothing and crops the still at its own grab scaling.
    ///
    /// OWNERSHIP: returns a new detached bitmap the caller disposes. Never null.
    /// ORDERING: none — there is no pre-step. Callers must NOT hide the overlay themselves; that double-hides.
    /// POST-CONDITION: on the live path the overlay is left hidden (every caller closes it afterwards); on the
    /// frozen path the overlay is untouched and still visible.
    /// </summary>
    public Task<SKBitmap> CommitAsync(CancellationToken ct = default) =>
        CommitCoreAsync(_commitProvider(), ct);

    /// <summary>Plain pixels — no annotations, no translation compositing, no cursor. Quick-OCR's variant of
    /// <see cref="CommitAsync"/>; same ownership, ordering and error contract.</summary>
    public Task<SKBitmap> CommitPlainAsync(CancellationToken ct = default) =>
        CommitCoreAsync(_commitProvider().AsPlain(), ct);

    private async Task<SKBitmap> CommitCoreAsync(SelectionCommit commit, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Take a lease for the whole commit so a concurrent unfreeze cannot dispose the pixels mid-crop.
        using var lease = LeaseFrozenStill();
        if (lease != null)
        {
            ct.ThrowIfCancellationRequested();
            // The offset is already baked into the full-desktop still, and the crop uses the scaling the still
            // was GRABBED at — not the overlay's current VisualScaling, which a DPI change can have moved.
            return FreezeFrameCompositor.CropWithAnnotations(
                lease.Still, commit.Selection, lease.GrabScaling, commit.Annotations);
        }

        await _visibility.HideAndWaitForCaptureAsync(HideOverlay ?? (() => { }), ct);

        var g = commit.Geometry;
        return commit.Annotations != null
            ? await _capture.CaptureScreenWithAnnotationsAsync(
                commit.Selection, g.ScreenOffset, g.VisualScaling, commit.Annotations,
                commit.TranslationSelections, commit.TranslatedBlocks, commit.IncludeCursor)
            : await _capture.CaptureScreenAsync(
                commit.Selection, g.ScreenOffset, g.VisualScaling, includeCursor: false);
    }

    // ---- Explicit borrow, for consumers that need the whole still rather than the selection ----

    /// <summary>
    /// Borrow the frozen still; null when live. The pixels stay valid until the lease is disposed even if the
    /// surface returns to live meanwhile — which is what makes handing the still to a background OCR thread safe.
    /// Never dispose <see cref="FrozenFrameLease.Still"/> yourself.
    /// </summary>
    public FrozenFrameLease? LeaseFrozenStill()
    {
        lock (_gate)
        {
            if (_state is not OverlaySurfaceState.Frozen frozen) return null;
            _leases++;
            return new FrozenFrameLease(this, frozen.Backdrop);
        }
    }

    private OverlaySurfaceState AdoptStill(SKBitmap still, double grabScaling, FrozenOrigin origin)
    {
        if (_disposed || _activity != OverlayActivity.Screenshot)
        {
            still.Dispose();
            return _state;
        }

        // TryCreate takes ownership and returns null (having disposed) when there is nothing showable — so the
        // display-conversion fallback is the absence of a Frozen state, not a rollback of a half-built one.
        var backdrop = OverlayBackdrop.TryCreate(still, grabScaling, _toDisplay);
        if (backdrop == null) return _state;

        if (_state is OverlaySurfaceState.Frozen previous)
        {
            RetireBackdrop(previous.Backdrop);
        }

        State = new OverlaySurfaceState.Frozen(backdrop, origin);
        return _state;
    }

    private void RetireBackdrop(OverlayBackdrop backdrop)
    {
        lock (_gate)
        {
            if (_leases > 0)
            {
                _retiring = backdrop;
                return;
            }
        }

        backdrop.Dispose();
    }

    internal void ReturnLease(OverlayBackdrop backdrop)
    {
        OverlayBackdrop? toDispose = null;

        lock (_gate)
        {
            _leases--;
            if (_leases <= 0 && ReferenceEquals(_retiring, backdrop))
            {
                toDispose = _retiring;
                _retiring = null;
            }
        }

        toDispose?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_state is OverlaySurfaceState.Frozen frozen)
        {
            RetireBackdrop(frozen.Backdrop);
        }

        _state = OverlaySurfaceState.Live.Instance;
        HideOverlay = null;
        RunGrabExcludingOverlay = null;
    }
}

/// <summary>
/// A ref-counted borrow of the frozen still. Thread-safe; dispose exactly once. Exists because the AI scan hands
/// the still to a background OCR thread that can outlive a toolbar unfreeze — previously that disposed native
/// Skia memory under the running scan.
/// </summary>
public sealed class FrozenFrameLease : IDisposable
{
    private readonly OverlaySurface _surface;
    private readonly OverlayBackdrop _backdrop;
    private int _disposed;

    internal FrozenFrameLease(OverlaySurface surface, OverlayBackdrop backdrop)
    {
        _surface = surface;
        _backdrop = backdrop;
    }

    /// <summary>The full-desktop still, in physical pixels. Borrowed — do not dispose.</summary>
    public SKBitmap Still => _backdrop.Pixels;

    /// <summary>The scaling <see cref="Still"/> was grabbed at. Any projection back to overlay coordinates must
    /// use this, not the overlay's current VisualScaling.</summary>
    public double GrabScaling => _backdrop.Scaling;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _surface.ReturnLease(_backdrop);
    }
}
