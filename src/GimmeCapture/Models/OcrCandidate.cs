using System;
using Avalonia;

namespace GimmeCapture.Models;

public enum OcrCandidateKind
{
    Paragraph,
    Line
}

public sealed class OcrCandidate
{
    public OcrCandidate(Rect bounds, OcrCandidateKind kind, int groupId)
    {
        Bounds = bounds;
        Kind = kind;
        GroupId = groupId;
    }

    public Rect Bounds { get; }
    public OcrCandidateKind Kind { get; }
    public int GroupId { get; }
    public double Area => Bounds.Width * Bounds.Height;

    public bool IsSameIdentity(OcrCandidate? other)
    {
        if (other is null)
        {
            return false;
        }

        return Kind == other.Kind
            && GroupId == other.GroupId
            && Math.Abs(Bounds.X - other.Bounds.X) <= 0.1
            && Math.Abs(Bounds.Y - other.Bounds.Y) <= 0.1
            && Math.Abs(Bounds.Width - other.Bounds.Width) <= 0.1
            && Math.Abs(Bounds.Height - other.Bounds.Height) <= 0.1;
    }
}
