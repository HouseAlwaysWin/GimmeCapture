using GimmeCapture.Models;

namespace GimmeCapture.Services.Abstractions;

public interface ISnipWindowFactory
{
    void Open(object mainViewModel, CaptureMode mode);

    object? GetActiveViewModel();
}
