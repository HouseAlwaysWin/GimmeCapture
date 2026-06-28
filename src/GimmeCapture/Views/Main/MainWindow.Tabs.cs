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
// Split out of MainWindow.axaml.cs by concern — no behavior change.
public partial class MainWindow
{
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
            case 2:
                _recordTabHost ??= this.FindControl<ContentControl>("RecordTabHost");
                _recordTabHost!.Content ??= new SettingsRecordTab();
                break;
            // index 3 = Compress tab (SettingsCompressTab) — non-lazy, declared inline in MainWindow.axaml.
            case 4:
                if (DataContext is MainWindowViewModel translationVm)
                {
                    translationVm.RefreshLlamaModelCatalog();
                }

                _translationTabHost ??= this.FindControl<ContentControl>("TranslationTabHost");
                _translationTabHost!.Content ??= new SettingsTranslationTab();
                break;
            case 6:
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.EnsureModulesInitialized();
                }

                _modulesTabHost ??= this.FindControl<ContentControl>("ModulesTabHost");
                _modulesTabHost!.Content ??= new SettingsModulesTab();
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
