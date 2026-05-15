using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Abstractions;

public interface ISnipWindowFactory
{
    void Open(MainWindowViewModel mainViewModel, CaptureMode mode);

    SnipWindowViewModel? GetActiveViewModel();
}
