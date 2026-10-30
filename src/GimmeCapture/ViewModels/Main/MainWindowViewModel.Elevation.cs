using System;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Running as administrator. While a window run as administrator has the foreground, Windows withholds every hotkey from
/// an app that is not elevated (see <see cref="ElevatedLaunch"/>): this is the setting that elevates the app, and the
/// one-time explanation that offers it.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>What this process can do about administrator rights; tests set their own.</summary>
    internal Func<AdministratorRights> AdministratorRightsProvider { get; set; } = static () => ElevatedLaunch.Current;

    /// <summary>The setting can do something here: on Windows, for an account that elevates as itself.</summary>
    public bool CanRunAsAdministrator => AdministratorRightsProvider() != AdministratorRights.Unavailable;

    /// <summary>Offers to restart as administrator now that the setting has been turned on.</summary>
    public Action? RequestRestartAsAdministratorAction { get; set; }

    /// <summary>Shows the one-time explanation (<see cref="OfferRunAsAdministratorOnce"/>).</summary>
    public Action? RequestElevatedWindowPromptAction { get; set; }

    private bool _runAsAdministrator;
    public bool RunAsAdministrator
    {
        get => _runAsAdministrator;
        set
        {
            // Turned on in the settings while not elevated: offer the restart now rather than leave it to the next launch.
            if (SetRunAsAdministrator(value) && value && !_isDataLoading
                && AdministratorRightsProvider() == AdministratorRights.CanElevate)
            {
                RequestRestartAsAdministratorAction?.Invoke();
            }
        }
    }

    /// <summary>Sets and saves the setting without offering a restart. True when it changed.</summary>
    internal bool SetRunAsAdministrator(bool value)
    {
        if (_runAsAdministrator == value)
        {
            return false;
        }

        this.RaiseAndSetIfChanged(ref _runAsAdministrator, value, nameof(RunAsAdministrator));
        if (!_isDataLoading)
        {
            _settingsService.Settings.RunAsAdministrator = value;
            MarkModifiedAndQueueSettingsSave();
        }

        return true;
    }

    /// <summary>
    /// A window run as administrator took the foreground while this process is not elevated (the hotkey service raises
    /// it). Explains, once per install, that the hotkeys do nothing over such a window, and offers to run as
    /// administrator. Not during a capture or recording, which a dialog would get in the way of: a later window asks then.
    /// </summary>
    internal void OfferRunAsAdministratorOnce()
    {
        if (RunAsAdministrator
            || _settingsService.Settings.ElevatedWindowNoticeShown
            || AdministratorRightsProvider() != AdministratorRights.CanElevate
            || IsCaptureInProgress)
        {
            return;
        }

        _settingsService.Settings.ElevatedWindowNoticeShown = true;
        QueueSettingsSave();
        RequestElevatedWindowPromptAction?.Invoke();
    }

    /// <summary>A capture overlay is open or a recording runs: no moment for a dialog, nor to restart the app.</summary>
    internal bool IsCaptureInProgress =>
        GetActiveSnipViewModelAction?.Invoke() != null
        || (_recordingService.IsValueCreated && _recordingService.Value.State != RecordingState.Idle);
}
