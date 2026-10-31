using System;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Software rendering (<see cref="Models.AppSettings.SoftwareRendering"/>): every window drawn on the CPU, which keeps what
/// Direct3D and the graphics driver hold out of the process. The renderer is picked as the app starts, so a change only
/// takes effect at the next launch, and the bootstrapper offers to restart.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The setting can do something here: only the Windows build picks its renderer from it.</summary>
    public bool CanUseSoftwareRendering => OperatingSystem.IsWindows();

    /// <summary>Offers to restart now that the setting has changed; the bootstrapper asks only when it differs from what runs.</summary>
    public Action? RequestRestartForRenderingAction { get; set; }

    private bool _softwareRendering;
    public bool SoftwareRendering
    {
        get => _softwareRendering;
        set
        {
            if (_softwareRendering == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _softwareRendering, value);
            if (!_isDataLoading)
            {
                _settingsService.Settings.SoftwareRendering = value;
                MarkModifiedAndQueueSettingsSave();
                RequestRestartForRenderingAction?.Invoke();
            }
        }
    }
}
