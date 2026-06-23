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

public partial class MainWindow : Window
{
    private readonly IDownloadWindowService _downloadWindowService;
    private readonly IToastService _toastService;
    private readonly ISnipWindowFactory _snipWindowFactory;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public MainWindow() : this(new MainWindowViewModel(), new NoOpDownloadWindowService(), new NoOpToastService(), new NoOpSnipWindowFactory())
    {
    }

    public MainWindow(
        MainWindowViewModel? viewModel,
        IDownloadWindowService? downloadWindowService,
        IToastService? toastService,
        ISnipWindowFactory? snipWindowFactory)
    {
        _downloadWindowService = downloadWindowService ?? new NoOpDownloadWindowService();
        _toastService = toastService ?? new NoOpToastService();
        _snipWindowFactory = snipWindowFactory ?? new NoOpSnipWindowFactory();

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
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await vm.SaveSettingsAsync();
            });
        }
    }

    private void OnPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.IsVisibleProperty && IsVisible)
        {
            ProcessMemoryTrimService.NotifyActivity("main-window-visible");
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
