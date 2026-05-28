using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private bool _hasLoadedReleases;
    private bool _isLoadingReleases;
    public bool IsLoadingReleases
    {
        get => _isLoadingReleases;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingReleases, value);
    }

    private string _releaseLoadError = string.Empty;
    public string ReleaseLoadError
    {
        get => _releaseLoadError;
        private set => this.RaiseAndSetIfChanged(ref _releaseLoadError, value);
    }

    private ReleaseInfo? _selectedRelease;
    public ReleaseInfo? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (_selectedRelease == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedRelease, value);
            this.RaisePropertyChanged(nameof(CanInstallSelectedRelease));
            this.RaisePropertyChanged(nameof(SelectedReleasePublishedText));
            this.RaisePropertyChanged(nameof(SelectedReleaseDisplayName));
        }
    }

    public ObservableCollection<ReleaseInfo> AvailableReleases { get; } = new();
    public bool HasReleaseLoadError => !string.IsNullOrWhiteSpace(ReleaseLoadError);
    public string CurrentVersionLabel => $"v{AppVersion.TrimStart('v')}";
    public string SelectedReleaseDisplayName => SelectedRelease?.TagName ?? CurrentVersionLabel;
    public string SelectedReleasePublishedText =>
        SelectedRelease?.PublishedAt is DateTimeOffset publishedAt
            ? publishedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            : "-";
    public bool CanInstallSelectedRelease =>
        SelectedRelease != null
        && !string.Equals(SelectedRelease.NormalizedVersion, AppVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase)
        && SelectedRelease.GetPreferredZipAsset() != null
        && !UpdateService.IsDownloading;

    private void InitializeAboutCommands()
    {
        RefreshReleaseCatalogCommand = ReactiveCommand.CreateFromTask(() => LoadAvailableReleasesAsync(true));
        InstallSelectedReleaseCommand = ReactiveCommand.CreateFromTask(InstallSelectedReleaseAsync);
    }

    public async Task LoadAvailableReleasesAsync(bool forceRefresh = false)
    {
        if (_hasLoadedReleases && !forceRefresh)
        {
            return;
        }

        try
        {
            IsLoadingReleases = true;
            ReleaseLoadError = string.Empty;
            this.RaisePropertyChanged(nameof(HasReleaseLoadError));

            var releases = await UpdateService.GetAvailableReleasesAsync();
            AvailableReleases.Clear();
            foreach (var release in releases)
            {
                AvailableReleases.Add(release);
            }

            _hasLoadedReleases = true;
            SelectedRelease = FindMatchingCurrentRelease() ?? (AvailableReleases.Count > 0 ? AvailableReleases[0] : null);
        }
        catch (Exception ex)
        {
            ReleaseLoadError = ex.Message;
            this.RaisePropertyChanged(nameof(HasReleaseLoadError));
        }
        finally
        {
            IsLoadingReleases = false;
        }
    }

    private async Task InstallSelectedReleaseAsync()
    {
        if (!CanInstallSelectedRelease || SelectedRelease == null)
        {
            return;
        }

        var zipPath = await UpdateService.DownloadUpdateAsync(SelectedRelease);
        if (string.IsNullOrEmpty(zipPath))
        {
            SetStatus("StatusError");
            return;
        }

        var readyMessage = string.Format(
            LocalizationService.Instance["InstallSelectedVersionReady"],
            SelectedRelease.TagName);

        bool? readyResult = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var mainWindow = _windowManager.GetMainWindow();
            if (mainWindow == null || ShowUpdateDialogAction == null)
            {
                return false;
            }

            if (mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
            }

            mainWindow.Activate();
            mainWindow.Show();
            return await ShowUpdateDialogAction(readyMessage, true);
        });

        if (readyResult == true)
        {
            UpdateService.ApplyUpdate(zipPath, SelectedRelease.NormalizedVersion);
        }
    }

    private ReleaseInfo? FindMatchingCurrentRelease()
    {
        foreach (var release in AvailableReleases)
        {
            if (string.Equals(release.NormalizedVersion, AppVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase))
            {
                return release;
            }
        }

        return null;
    }
}
