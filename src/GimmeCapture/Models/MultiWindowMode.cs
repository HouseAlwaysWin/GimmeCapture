namespace GimmeCapture.Models;

/// <summary>
/// How multiple picked windows are recorded when more than one window is selected in the record
/// capture-scope picker.
/// </summary>
public enum MultiWindowMode
{
    /// <summary>Tile every window onto one canvas and encode a single video.</summary>
    Composite,

    /// <summary>Record each window to its own separate video file simultaneously.</summary>
    Separate,
}
