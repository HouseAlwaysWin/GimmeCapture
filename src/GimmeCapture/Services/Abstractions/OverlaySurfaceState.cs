using System;
using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;
using SkiaSharp;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// The overlay activity that owns the surface. Only <see cref="Screenshot"/> may hold a frozen still —
/// recording, translation and manual scrolling capture are inherently live. Freeze-frame's mode gate used to
/// live in two places that could drift (the factory's CaptureMode allow-list and the toolbar toggle's SnipMode
/// guard) and was never enforced on a mode CHANGE; this enum is the single gate.
/// </summary>
public enum OverlayActivity
{
    Screenshot,
    Recording,
    Translation,
    ManualScrolling
}

/// <summary>
/// The overlay's geometry trio. Always passed together because the three are only meaningful together:
/// a viewport size with the wrong scaling, or an offset from a different monitor, silently misaligns a grab.
/// </summary>
public readonly record struct OverlayGeometry(Size ViewportSize, PixelPoint ScreenOffset, double VisualScaling)
{
    /// <summary>False before the View has laid the overlay out — a grab this size would be meaningless.</summary>
    public bool IsUsable => ViewportSize.Width > 1 && ViewportSize.Height > 1;

    /// <summary>Scaling normalised so a not-yet-initialised 0 can never divide or multiply a crop to nothing.</summary>
    public double SafeScaling => VisualScaling <= 0 ? 1.0 : VisualScaling;
}

/// <summary>
/// The opaque still an overlay paints behind its selection UI, in the two forms its consumers need — Skia (for
/// cropping and OCR) and Avalonia (for the background Image) — plus the scaling they were captured at.
/// Bundling them is the point: the three used to be separate fields that could disagree, and the crop had to
/// remember to use the grab scaling rather than the overlay's current <c>VisualScaling</c>.
/// </summary>
public sealed class OverlayBackdrop : IDisposable
{
    private OverlayBackdrop(SKBitmap pixels, Avalonia.Media.Imaging.Bitmap? image, double scaling)
    {
        Pixels = pixels;
        Image = image;
        Scaling = scaling;
    }

    /// <summary>Physical pixels covering the whole desktop, origin = overlay origin. Owned by this backdrop.</summary>
    public SKBitmap Pixels { get; }

    /// <summary>Display-ready copy bound to the overlay's full-window background Image. Owned by this backdrop.
    /// Null only on a host with no render platform (unit tests), where there is nothing to paint anyway.</summary>
    public Avalonia.Media.Imaging.Bitmap? Image { get; }

    /// <summary>
    /// The DIP→physical scale <see cref="Pixels"/> were captured at. Crops must use this and NOT the overlay's
    /// current VisualScaling — a DPI change after the grab makes the two differ.
    /// </summary>
    public double Scaling { get; }

    /// <summary>
    /// Takes ownership of <paramref name="pixels"/>. Returns null — having disposed them — when there is nothing
    /// to show: a still the user cannot SEE must never become the still we commit from, or they would select
    /// against the live screen while the capture came from elsewhere. This null return IS the display-conversion
    /// fallback; there is no half-built backdrop to roll back.
    /// </summary>
    /// <param name="toDisplay">
    /// Skia→Avalonia conversion, injected because the real one needs a render platform — which is why the frozen
    /// path used to be unreachable from a unit test. Two distinct nulls, deliberately:
    /// a <b>null delegate</b> means this host has no display surface at all, so the backdrop is built with a null
    /// <see cref="Image"/> and freeze still activates; a <b>present delegate returning null</b> means the
    /// conversion failed on a host that should have one, so there is nothing showable and the caller stays live.
    /// </param>
    public static OverlayBackdrop? TryCreate(
        SKBitmap? pixels,
        double scaling,
        Func<SKBitmap, Avalonia.Media.Imaging.Bitmap?>? toDisplay)
    {
        if (pixels == null || pixels.Width <= 0 || pixels.Height <= 0)
        {
            pixels?.Dispose();
            return null;
        }

        if (toDisplay == null)
        {
            return new OverlayBackdrop(pixels, null, scaling > 0 ? scaling : 1.0);
        }

        Avalonia.Media.Imaging.Bitmap? image;
        try
        {
            image = toDisplay(pixels);
        }
        catch (Exception)
        {
            // A conversion that throws is a conversion that failed; the caller falls back to live either way.
            image = null;
        }

        if (image == null)
        {
            pixels.Dispose();
            return null;
        }

        return new OverlayBackdrop(pixels, image, scaling > 0 ? scaling : 1.0);
    }

    /// <summary>The production Skia→Avalonia conversion. Needs a render platform.</summary>
    public static Avalonia.Media.Imaging.Bitmap? DefaultToDisplay(SKBitmap sk) =>
        GimmeCapture.ViewModels.Floating.FloatingBitmapConversionHelper
            .TryCreateDetachedBitmapFromSkBitmap(sk, out var display, out _)
            ? display
            : null;

    public void Dispose()
    {
        Pixels.Dispose();
        Image?.Dispose();
    }
}

/// <summary>
/// What the snip overlay's pixels MEAN right now. Exactly two cases exist and the hierarchy is closed by a
/// private base constructor, so a caller can neither invent a third state nor observe the old
/// "IsFrozenFrameActive == true but the still is null" pair — <see cref="Frozen"/> cannot be constructed
/// without a backdrop, so the compound null-check every caller used to write is unrepresentable.
/// Instances are immutable; a transition publishes a new one.
/// </summary>
public abstract class OverlaySurfaceState
{
    private OverlaySurfaceState()
    {
    }

    /// <summary>The overlay is see-through: the live desktop underneath is the truth, so a commit must hide the
    /// overlay and grab.</summary>
    public sealed class Live : OverlaySurfaceState
    {
        internal Live()
        {
        }

        public static readonly Live Instance = new();
    }

    /// <summary>A still of the whole desktop is the truth: the overlay paints it opaquely and a commit crops it.</summary>
    public sealed class Frozen : OverlaySurfaceState
    {
        internal Frozen(OverlayBackdrop backdrop, FrozenOrigin origin)
        {
            Backdrop = backdrop ?? throw new ArgumentNullException(nameof(backdrop));
            Origin = origin;
        }

        public OverlayBackdrop Backdrop { get; }

        /// <summary>Only <see cref="FrozenOrigin.PreOverlay"/> can contain a shell light-dismiss popup — a
        /// mid-session freeze holds whatever survived the overlay appearing over it.</summary>
        public FrozenOrigin Origin { get; }
    }
}

/// <summary>Which of the two acquisition mechanics produced a frozen still.</summary>
public enum FrozenOrigin
{
    /// <summary>Grabbed by the factory before the overlay was shown, so it predates (and can contain) a shell
    /// popup that any full-screen overlay would have dismissed.</summary>
    PreOverlay,

    /// <summary>Grabbed mid-session by the toolbar toggle, with the overlay excluded from the grab.</summary>
    MidSession
}

/// <summary>
/// A caller's declaration of what it wants out of the selection. Built by the surface from its commit provider,
/// never assembled at a call site — that is what keeps <c>CommitAsync()</c> argument-free.
/// </summary>
public readonly record struct SelectionCommit
{
    private SelectionCommit(
        Rect selection,
        OverlayGeometry geometry,
        IReadOnlyList<Annotation>? annotations,
        IReadOnlyList<UserSelectionRect>? translationSelections,
        IReadOnlyList<TranslatedBlock>? translatedBlocks,
        bool includeCursor)
    {
        Selection = selection;
        Geometry = geometry;
        Annotations = annotations;
        TranslationSelections = translationSelections;
        TranslatedBlocks = translatedBlocks;
        IncludeCursor = includeCursor;
    }

    public Rect Selection { get; }
    public OverlayGeometry Geometry { get; }

    /// <summary>Null means plain pixels (Quick-OCR).</summary>
    public IReadOnlyList<Annotation>? Annotations { get; }

    /// <summary>Live-path-only compositing inputs. The frozen path structurally cannot use them: freeze is
    /// screenshot-only, so translation overlays never coexist with it.</summary>
    public IReadOnlyList<UserSelectionRect>? TranslationSelections { get; }
    public IReadOnlyList<TranslatedBlock>? TranslatedBlocks { get; }
    public bool IncludeCursor { get; }

    public static SelectionCommit Annotated(
        Rect selection,
        OverlayGeometry geometry,
        IReadOnlyList<Annotation> annotations,
        IReadOnlyList<UserSelectionRect>? translationSelections = null,
        IReadOnlyList<TranslatedBlock>? translatedBlocks = null,
        bool includeCursor = false) =>
        new(selection, geometry, annotations, translationSelections, translatedBlocks, includeCursor);

    public static SelectionCommit Plain(Rect selection, OverlayGeometry geometry) =>
        new(selection, geometry, null, null, null, false);

    /// <summary>This commit with every composited layer stripped — what Quick-OCR wants from the same context.</summary>
    public SelectionCommit AsPlain() => Plain(Selection, Geometry);

    public bool HasUsableSelection => Selection.Width > 0 && Selection.Height > 0;
}
