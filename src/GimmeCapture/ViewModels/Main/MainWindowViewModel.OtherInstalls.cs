using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private OtherInstallsViewModel? _otherInstalls;

    /// <summary>The Modules tab's list of the other copies of GimmeCapture on this PC and the data each keeps here.</summary>
    public OtherInstallsViewModel OtherInstalls => _otherInstalls ??= CreateOtherInstalls();

    private OtherInstallsViewModel CreateOtherInstalls()
    {
        string instanceDirectory = AppStoragePaths.GetInstallInstanceDirectory(RuntimePathProvider.GetExecutableDirectory());
        return new OtherInstallsViewModel(
            Path.GetDirectoryName(instanceDirectory)!,
            Path.GetFileName(instanceDirectory),
            SingleInstanceGuard.IsRunning,
            (title, message) => ConfirmAction?.Invoke(title, message, false) ?? Task.FromResult(false),
            (title, message) => ConfirmAction?.Invoke(title, message, true) ?? Task.CompletedTask,
            Localization);
    }
}
