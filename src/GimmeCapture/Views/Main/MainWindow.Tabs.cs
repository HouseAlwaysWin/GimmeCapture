using System;
using Avalonia.Controls;
using Avalonia;
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
                _recordTabHost!.Content ??= new SettingsRecordTab();
                break;
            case 3:
                if (DataContext is MainWindowViewModel compressVm)
                {
                    compressVm.EnsureCompressInitialized();
                }

                _compressTabHost ??= this.FindControl<ContentControl>("CompressTabHost");
                _compressTabHost!.Content ??= new SettingsCompressTab();
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
}
