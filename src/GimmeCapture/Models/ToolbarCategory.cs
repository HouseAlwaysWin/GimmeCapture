namespace GimmeCapture.Models;

/// <summary>
/// Top-level toolbar category for the floating pins. The bottom bar shows these as buttons; selecting one
/// reveals that category's contextual sub-toolbar above the content. <see cref="None"/> = nothing expanded.
/// </summary>
public enum ToolbarCategory
{
    None,
    Annotate,
    Edit,
    Redact,
    Output,
}
