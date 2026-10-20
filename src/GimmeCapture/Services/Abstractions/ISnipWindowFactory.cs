using System;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Abstractions;

public interface ISnipWindowFactory
{
    void Open(object mainViewModel, CaptureMode mode);

    object? GetActiveViewModel();

    /// <summary>
    /// Builds an overlay window and view model once, unseen, after <paramref name="delay"/> — so the first capture of
    /// a run does not pay for loading their code. Skipped if an overlay has opened by then.
    /// </summary>
    void ScheduleWarmUp(TimeSpan delay);
}
