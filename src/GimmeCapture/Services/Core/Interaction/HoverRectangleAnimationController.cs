using System;
using Avalonia;

namespace GimmeCapture.Services.Core.Interaction;

public sealed class HoverRectangleAnimationController
{
    private readonly TimeSpan _duration;
    private readonly double _dashSpeed;
    private Rect _fromRect;
    private Rect _toRect;
    private TimeSpan _elapsed;
    private bool _isAnimating;

    public HoverRectangleAnimationController(TimeSpan duration, double dashSpeed)
    {
        _duration = duration;
        _dashSpeed = dashSpeed;
    }

    public Rect CurrentRect { get; private set; }
    public double DashOffset { get; private set; }
    public bool IsVisible { get; private set; }

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

        DashOffset += _dashSpeed * elapsed.TotalSeconds;

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
