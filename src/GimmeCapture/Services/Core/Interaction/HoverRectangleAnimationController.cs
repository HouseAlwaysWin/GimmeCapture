using System;
using Avalonia;

namespace GimmeCapture.Services.Core.Interaction;

public sealed class HoverRectangleAnimationController
{
    /// <summary>How often <see cref="Advance"/> wants calling, unless <see cref="Economical"/> dashes only flow.</summary>
    public static readonly TimeSpan FullInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>How often an <see cref="Economical"/> outline's dashes step.</summary>
    public static readonly TimeSpan EconomicalInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>How long an <see cref="Economical"/> outline's dashes flow after the pointer last moved.</summary>
    public static readonly TimeSpan RestAfter = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _duration;
    private readonly double _dashSpeed;
    private Rect _fromRect;
    private Rect _toRect;
    private TimeSpan _elapsed;
    private TimeSpan _sincePointerMoved;
    private bool _isAnimating;

    public HoverRectangleAnimationController(TimeSpan duration, double dashSpeed)
    {
        _duration = duration;
        _dashSpeed = dashSpeed;
    }

    public Rect CurrentRect { get; private set; }
    public double DashOffset { get; private set; }
    public bool IsVisible { get; private set; }

    /// <summary>
    /// Paced for the processor (software rendering), where every frame costs a copy of the whole overlay to the screen,
    /// whatever changed: the dashes step every <see cref="EconomicalInterval"/> and stop flowing once the pointer has
    /// rested for <see cref="RestAfter"/>, the outline still shown. A move to a new target keeps the full rate. With the
    /// dashes flowing at the full rate an outline cost a fifth to a third of a core while the overlay sat open.
    /// </summary>
    public bool Economical { get; set; }

    /// <summary>How often <see cref="Advance"/> wants calling now.</summary>
    public TimeSpan TickInterval => Economical && !_isAnimating ? EconomicalInterval : FullInterval;

    /// <summary>Whether the next <see cref="Advance"/> would change anything: the outline moving, or its dashes flowing.</summary>
    public bool NeedsTicks => IsVisible && (_isAnimating || DashesFlow);

    private bool DashesFlow => !Economical || _sincePointerMoved < RestAfter;

    /// <summary>The pointer moved over the overlay: an <see cref="Economical"/> outline's dashes flow again.</summary>
    public void PointerMoved() => _sincePointerMoved = TimeSpan.Zero;

    public void SetTarget(Rect targetRect)
    {
        if (targetRect.Width <= 0 || targetRect.Height <= 0)
        {
            Hide();
            return;
        }

        if (!IsVisible || CurrentRect.Width <= 0 || CurrentRect.Height <= 0)
        {
            CurrentRect = targetRect;
            _fromRect = targetRect;
            _toRect = targetRect;
            _elapsed = TimeSpan.Zero;
            _isAnimating = false;
            IsVisible = true;
            return;
        }

        if (AreRectsClose(_toRect, targetRect))
        {
            return;
        }

        _fromRect = CurrentRect;
        _toRect = targetRect;
        _elapsed = TimeSpan.Zero;
        _isAnimating = true;
        IsVisible = true;
    }

    public void Advance(TimeSpan elapsed)
    {
        if (!IsVisible || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        if (DashesFlow)
        {
            DashOffset += _dashSpeed * elapsed.TotalSeconds;
        }

        _sincePointerMoved += elapsed;

        if (!_isAnimating)
        {
            return;
        }

        _elapsed += elapsed;
        double progress = Math.Clamp(_elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0.0, 1.0);
        double eased = EaseOutCubic(progress);
        CurrentRect = Lerp(_fromRect, _toRect, eased);

        if (progress >= 1.0)
        {
            CurrentRect = _toRect;
            _isAnimating = false;
        }
    }

    public void Hide()
    {
        IsVisible = false;
        CurrentRect = default;
        _fromRect = default;
        _toRect = default;
        _elapsed = TimeSpan.Zero;
        _isAnimating = false;
    }

    private static Rect Lerp(Rect fromRect, Rect toRect, double progress)
    {
        return new Rect(
            fromRect.X + ((toRect.X - fromRect.X) * progress),
            fromRect.Y + ((toRect.Y - fromRect.Y) * progress),
            fromRect.Width + ((toRect.Width - fromRect.Width) * progress),
            fromRect.Height + ((toRect.Height - fromRect.Height) * progress));
    }

    private static double EaseOutCubic(double value)
    {
        double inverse = 1.0 - value;
        return 1.0 - (inverse * inverse * inverse);
    }

    private static bool AreRectsClose(Rect left, Rect right)
    {
        const double tolerance = 0.1;
        return Math.Abs(left.X - right.X) <= tolerance
            && Math.Abs(left.Y - right.Y) <= tolerance
            && Math.Abs(left.Width - right.Width) <= tolerance
            && Math.Abs(left.Height - right.Height) <= tolerance;
    }
}
