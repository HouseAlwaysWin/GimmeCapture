using System;
using Avalonia.Controls;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Views.Main;
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

public partial class MainWindow : Window
{
    private readonly IDownloadWindowService _downloadWindowService;
    private IDisposable? _processingSubscription;

    public MainWindow() : this(new MainWindowViewModel(), new NoOpDownloadWindowService())
    {
    }

    public MainWindow(MainWindowViewModel? viewModel, IDownloadWindowService? downloadWindowService)
    {
        _downloadWindowService = downloadWindowService ?? new NoOpDownloadWindowService();

        InitializeComponent();
        DataContext = viewModel ?? new MainWindowViewModel();

        this.PropertyChanged += OnPropertyChanged;
        this.Closing += OnClosing;

        var tabControl = this.FindControl<TabControl>("MainTabControl");
        _recordTabHost = this.FindControl<ContentControl>("RecordTabHost");
        _translationTabHost = this.FindControl<ContentControl>("TranslationTabHost");
        _modulesTabHost = this.FindControl<ContentControl>("ModulesTabHost");
        _aboutTabHost = this.FindControl<ContentControl>("AboutTabHost");
        if (tabControl != null)
        {
            tabControl.SelectionChanged += (s, e) =>
            {
                EnsureLazyTabContent(tabControl.SelectedIndex);
                UpdateDownloadWindow();
            };
            EnsureLazyTabContent(tabControl.SelectedIndex);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
    }

    private void TitleBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private bool _isClosingFromDialog = false;
    private bool _isExiting = false;

    public void Shutdown()
    {
        _isExiting = true;
        _downloadWindowService.Close();
        Close();
    }

    /// <summary>
    /// Closes this window for good, hidden in the tray as it is: what it wired into the view model, which outlives it, is
    /// undone, so nothing keeps it and the memory it and its tabs hold can be collected. The next open builds a new one.
    /// </summary>
    internal void Release()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            UnwireViewModel(vm);
        }

        _isExiting = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isExiting || _isClosingFromDialog) return;

        // 點擊 X 關閉視窗時，攔截事件並隱藏視窗（縮小至系統匣）
        e.Cancel = true;
        Hide();
        UpdateDownloadWindow();
        ProcessMemoryTrimService.RequestIdleWorkingSetTrimAsync("main-window-tray")
            .Forget("MemoryTrim.MainWindowTray");

        // 如果有修改，仍可以在後台提示
        if (DataContext is MainWindowViewModel vm && vm.IsModified)
        {
            // Use Post to ensure we don't block the visual tree teardown (prevents PopupRoot/PlatformImpl null errors)
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => vm.SaveSettingsAsync().Forget("Settings.SaveOnClosing"));
        }
    }

    private void OnPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsVisibleProperty && IsVisible)
        {
            ProcessMemoryTrimService.NotifyActivity("main-window-visible");

            // The startup-time re-assert cannot repair a MISSING entry, because a missing entry is precisely what
            // stops the app launching at login — the code never runs. Opening the window by hand is the one
            // moment the app is alive with the entry gone, so it is where the repair has to happen.
            (DataContext as MainWindowViewModel)?.ReverifyStartupRegistration();

            // An update the start-up check found while this window was not up, as in a launch to the tray, is offered
            // now. Posted: its dialog needs this window on screen, which it is not quite yet while it is being shown.
            if (DataContext is MainWindowViewModel vm)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    vm.OfferPendingUpdate,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        }

        if (e.Property == Window.WindowStateProperty || e.Property == Window.IsVisibleProperty || e.Property == Window.BoundsProperty)
        {
            UpdateDownloadWindow();
        }
    }

    private void UpdateDownloadWindow()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _downloadWindowService.Update(this, vm, vm.IsProcessing);
    }
}
