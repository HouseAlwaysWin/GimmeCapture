using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// The Modules tab's list of the other copies of GimmeCapture on this PC and what each keeps in the data folder. Every
/// folder the app runs from has its own settings and AI modules there, so a copy moved, reinstalled elsewhere or deleted
/// leaves them behind. Removing one deletes its instance folder once the user confirms; a copy that is running is
/// listed without the option.
/// </summary>
public sealed class OtherInstallsViewModel : ViewModelBase
{
    private readonly string _instancesDirectory;
    private readonly string _currentKey;
    private readonly Func<string, bool> _isRunning;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Func<string, string, Task> _notify;
    private IReadOnlyList<OtherInstall> _installs = Array.Empty<OtherInstall>();

    /// <param name="confirm">Asks a yes/no question (title, message); true for yes.</param>
    /// <param name="notify">Tells the user something (title, message).</param>
    internal OtherInstallsViewModel(
        string instancesDirectory,
        string currentKey,
        Func<string, bool> isRunning,
        Func<string, string, Task<bool>> confirm,
        Func<string, string, Task> notify,
        ILocalizationService? localization = null)
        : base(localization)
    {
        _instancesDirectory = instancesDirectory;
        _currentKey = currentKey;
        _isRunning = isRunning;
        _confirm = confirm;
        _notify = notify;

        Localization.WhenAnyValue(x => x.CurrentLanguage).Skip(1).Subscribe(_ => Render());
    }

    public ObservableCollection<OtherInstallItem> Items { get; } = new();

    private bool _hasAny;
    public bool HasAny
    {
        get => _hasAny;
        private set => this.RaiseAndSetIfChanged(ref _hasAny, value);
    }

    private string _summary = string.Empty;
    public string Summary
    {
        get => _summary;
        private set => this.RaiseAndSetIfChanged(ref _summary, value);
    }

    /// <summary>Looks at the data folder again. The look reads every file's size, so it runs off the UI thread.</summary>
    public async Task RefreshAsync()
    {
        _installs = await Task.Run(() => OtherInstallInventory.Scan(_instancesDirectory, _currentKey, _isRunning));
        Render();
    }

    internal async Task RemoveAsync(OtherInstall install)
    {
        string title = Localization["OtherInstallsTitle"];
        string message = string.Format(
            Localization["ConfirmRemoveOtherInstall"], LocationText(install), FileSizeFormatter.Format(install.SizeBytes));
        if (!await _confirm(title, message))
        {
            return;
        }

        // Started since the list was drawn: its data is in use, so it stays, and the list says so once refreshed.
        string? failure = _isRunning(install.Key)
            ? Localization["OtherInstallRunning"]
            : await Task.Run(() => OtherInstallInventory.Remove(install));
        if (failure != null)
        {
            await _notify(title, string.Format(Localization["OtherInstallRemoveFailed"], failure));
        }

        await RefreshAsync();
    }

    private void Render()
    {
        Items.Clear();
        foreach (var install in _installs)
        {
            Items.Add(new OtherInstallItem(
                LocationText(install),
                DetailsText(install),
                !install.IsRunning,
                ReactiveCommand.CreateFromTask(() => RemoveAsync(install))));
        }

        HasAny = Items.Count > 0;
        Summary = string.Format(
            Localization["OtherInstallsSummary"],
            Items.Count,
            FileSizeFormatter.Format(_installs.Sum(install => install.SizeBytes)));
    }

    private string LocationText(OtherInstall install)
    {
        if (install.ExecutablePath == null)
        {
            return Localization["OtherInstallUnknownLocation"];
        }

        string location = System.IO.Path.GetDirectoryName(install.ExecutablePath) ?? install.ExecutablePath;
        return install.IsGone ? string.Format(Localization["OtherInstallGoneLocation"], location) : location;
    }

    private string DetailsText(OtherInstall install)
    {
        var parts = new List<string>
        {
            FileSizeFormatter.Format(install.SizeBytes),
            string.Format(Localization["OtherInstallLastUsed"], install.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd")),
        };
        if (install.Version != null)
        {
            parts.Add(string.Format(Localization["OtherInstallVersion"], install.Version));
        }

        if (install.IsRunning)
        {
            parts.Add(Localization["OtherInstallRunning"]);
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>One row of <see cref="OtherInstallsViewModel"/>.</summary>
public sealed class OtherInstallItem(string location, string details, bool canRemove, ICommand removeCommand)
{
    public string Location { get; } = location;

    public string Details { get; } = details;

    public bool CanRemove { get; } = canRemove;

    public ICommand RemoveCommand { get; } = removeCommand;
}
