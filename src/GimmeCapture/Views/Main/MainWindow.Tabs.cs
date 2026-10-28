using System;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Views.Main;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;
using ReactiveUI.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Views.Main.Tabs;

namespace GimmeCapture.Views.Main;

// Settings tab lazy-loading + modules-tab navigation for MainWindow.
// Every tab but General (selected at start) is built the first time it is shown: building them all up front was
// over half a second of the start-up's UI-thread time.
public partial class MainWindow
{
    private ContentControl? _snipTabHost;
    private ContentControl? _compressTabHost;
    private ContentControl? _hotkeysTabHost;
    private ContentControl? _historyTabHost;
    private ContentControl? _recordTabHost;
    private ContentControl? _translationTabHost;
    private ContentControl? _modulesTabHost;
    private ContentControl? _aboutTabHost;

    private void OpenModulesTab()
    {
        var tabControl = this.FindControl<TabControl>("MainTabControl");
        if (tabControl == null)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        tabControl.SelectedIndex = 6;
    }

    // The microphone list is read on the thread pool the first time the tab is shown, and the tab is built once it is in,
    // so its device picker never binds to a list still being filled. The read takes a frame or a few.
    private async Task ShowRecordTabAsync(ContentControl host)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            // The tab's codec list and GIF choice wait on the hardware probe, which the app otherwise starts after the
            // first frame.
            vm.StartHardwareProbe();

            Task ready = vm.RecordingSettings.LoadMicInputDevicesAsync();
            if (!ready.IsCompleted)
            {
                host.Content = new Panel(); // keeps a second visit from starting this again
                try
                {
                    await ready;
                }
                catch (Exception ex)
                {
                    // Still show the tab, with an empty microphone list.
                    AppLog.Warning("Record.LoadMicrophones", ex);
                }
            }
        }

        host.Content = new SettingsRecordTab();
    }

    private void EnsureLazyTabContent(int selectedIndex)
    {
        switch (selectedIndex)
        {
            case 1:
                _snipTabHost ??= this.FindControl<ContentControl>("SnipTabHost");
                _snipTabHost!.Content ??= new SettingsSnipTab();
                break;
            case 2:
                _recordTabHost ??= this.FindControl<ContentControl>("RecordTabHost");
                if (_recordTabHost!.Content == null)
                {
                    ShowRecordTabAsync(_recordTabHost).Forget("Record.ShowTab");
                }
                break;
            case 3:
                _compressTabHost ??= this.FindControl<ContentControl>("CompressTabHost");
                if (_compressTabHost!.Content == null)
                {
                    ShowCompressTabAsync(_compressTabHost).Forget("Compress.ShowTab");
                }
                break;
            case 4:
                if (DataContext is MainWindowViewModel translationVm)
                {
                    translationVm.RefreshLlamaModelCatalog();
                }

                _translationTabHost ??= this.FindControl<ContentControl>("TranslationTabHost");
                _translationTabHost!.Content ??= new SettingsTranslationTab();
                break;
            case 5:
                _hotkeysTabHost ??= this.FindControl<ContentControl>("HotkeysTabHost");
                _hotkeysTabHost!.Content ??= new SettingsHotkeysTab();
                break;
            case 6:
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.EnsureModulesInitialized();
                    vm.OtherInstalls.RefreshAsync().Forget("Modules.OtherInstalls");
                }

                _modulesTabHost ??= this.FindControl<ContentControl>("ModulesTabHost");
                _modulesTabHost!.Content ??= new SettingsModulesTab();
                break;
            case 7:
                _historyTabHost ??= this.FindControl<ContentControl>("HistoryTabHost");
                _historyTabHost!.Content ??= new SettingsHistoryTab();
                break;
            case 8:
                if (DataContext is MainWindowViewModel aboutVm)
                {
                    aboutVm.LoadAvailableReleasesAsync().Forget("About.LoadAvailableReleases");
                }

                _aboutTabHost ??= this.FindControl<ContentControl>("AboutTabHost");
                _aboutTabHost!.Content ??= new SettingsAboutTab();
                break;
        }
    }

    // The read usually takes a blink; a progress bar shown for that long would only flash.
    private static readonly TimeSpan CompressTabProgressDelay = TimeSpan.FromMilliseconds(300);

    // The Compress tab's saved queue and settings are read from disk in the background the first time it is shown, and
    // the tab is built only once they are in: earlier, it would offer an empty queue to act on, and settings the restore
    // then overwrites. A progress bar holds its place meanwhile, visible once the read takes long enough to notice.
    private async Task ShowCompressTabAsync(ContentControl host)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            Task ready = vm.EnsureCompressInitializedAsync();
            IDisposable? revealProgress = null;
            if (!ready.IsCompleted)
            {
                var progress = new ProgressBar
                {
                    IsIndeterminate = true,
                    IsVisible = false,
                    Width = 160,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                host.Content = progress; // also keeps a second visit from starting this again
                revealProgress = DispatcherTimer.RunOnce(() => progress.IsVisible = true, CompressTabProgressDelay);
            }

            try
            {
                await ready;
            }
            catch (Exception ex)
            {
                // Still show the tab: whatever part of the restore failed is in the log.
                AppLog.Error("Compress.Initialize", ex);
            }
            finally
            {
                revealProgress?.Dispose();
            }
        }

        host.Content = new SettingsCompressTab();
    }
}
