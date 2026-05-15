using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Platforms.Avalonia;

public interface ISnipWindowFactory
{
    void Open(MainWindowViewModel mainViewModel, CaptureMode mode);

    SnipWindowViewModel? GetActiveViewModel();
}
