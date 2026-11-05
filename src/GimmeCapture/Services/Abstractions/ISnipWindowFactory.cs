using System;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Abstractions;

public interface ISnipWindowFactory
{
    void Open(object mainViewModel, CaptureMode mode);

    object? GetActiveViewModel();

    /// <summary>
    /// Builds an overlay window and view model once, unseen, after <paramref name="delay"/> — so the first capture of
    /// a run does not pay for loading their code; skipped if an overlay has opened by then. Then a pin, with the app's
    /// services from <paramref name="mainViewModel"/>, drawn off screen once, unless an overlay is open.
    /// </summary>
    void ScheduleWarmUp(object mainViewModel, TimeSpan delay);
}
