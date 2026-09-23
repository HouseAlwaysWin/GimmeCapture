using System;

namespace GimmeCapture.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for behaviour that only exists on Windows. Off Windows the test is reported as
/// SKIPPED — unlike an early <c>return</c> inside the test body, which the net10.0 (Linux) run counts as a pass
/// for something it never checked.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only behaviour.";
        }
    }
}
