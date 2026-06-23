namespace GimmeCapture.Views.Main;

/// <summary>
/// Which handle of a selection / translation box is being dragged during a resize.
/// Promoted from a private nested enum in <c>SnipWindow</c> to an internal top-level
/// type so the pure resize math (<see cref="SelectionResizeMath"/>) and its unit
/// tests can reference it.
/// </summary>
internal enum ResizeDirection
{
    None, TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right
}
