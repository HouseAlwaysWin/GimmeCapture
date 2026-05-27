using System;
using Avalonia;

namespace GimmeCapture.Models;

public enum WindowCandidateKind
{
    TopLevel,
    ChildControl
}

public sealed class WindowCandidate
{
    public WindowCandidate(
        Rect bounds,
        IntPtr hwnd,
        IntPtr parentHwnd,
        IntPtr rootHwnd,
        int zOrder,
        WindowCandidateKind kind)
    {
        Bounds = bounds;
        Hwnd = hwnd;
        ParentHwnd = parentHwnd;
        RootHwnd = rootHwnd == IntPtr.Zero ? hwnd : rootHwnd;
        ZOrder = zOrder;
        Kind = kind;
    }

    public Rect Bounds { get; }
    public IntPtr Hwnd { get; }
    public IntPtr ParentHwnd { get; }
    public IntPtr RootHwnd { get; }
    public int ZOrder { get; }
    public WindowCandidateKind Kind { get; }
    public bool IsChild => Kind == WindowCandidateKind.ChildControl;
    public double Area => Bounds.Width * Bounds.Height;

    public bool IsSameIdentity(WindowCandidate? other)
    {
        if (other is null)
        {
            return false;
        }

        return Hwnd == other.Hwnd
            && ParentHwnd == other.ParentHwnd
            && RootHwnd == other.RootHwnd
            && Kind == other.Kind
            && ZOrder == other.ZOrder;
    }
}
