using System;
using Avalonia.Controls;
using Avalonia;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Views.Main;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Platforms.Windows;
using ReactiveUI;
using System.Reactive.Linq;

namespace GimmeCapture.Views.Main;

public partial class MainWindow : Window
{
    private readonly IDownloadWindowService _downloadWindowService = new AvaloniaDownloadWindowService();
    private readonly IScreenLayoutService _screenLayoutService = new AvaloniaScreenLayoutService();
    private readonly IWindowLayerService _windowLayerService = new AvaloniaWindowLayerService();
    private readonly IWindowManager _windowManager = new AvaloniaWindowManager();

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public MainWindow()
    {
        InitializeComponent();
        
        this.PropertyChanged += OnPropertyChanged;
        this.Closing += OnClosing;

        var tabControl = this.FindControl<TabControl>("MainTabControl");
        if (tabControl != null)
        {
            tabControl.SelectionChanged += (s, e) => UpdateDownloadWindow();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainWindowViewModel vm)
        {
            // Initialize Hotkey Service with this Window AFTER it has a handle
            vm.HotkeyService.Initialize(this);
        }
    }

    private void TopBar_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
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
        if (e.Property == Window.WindowStateProperty || e.Property == Window.IsVisibleProperty || e.Property == Window.BoundsProperty)
        {
            UpdateDownloadWindow();
        }
    }


    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PickFolderAction = async () =>
            {
                var storage = this.StorageProvider;
                var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "選擇錄影儲存資料夾",
                    AllowMultiple = false
                });

                return folders.Count > 0 ? folders[0].Path.LocalPath : null;
            };

            vm.ConfirmAction = async (title, message, isOkOnly) =>
            {
                var mode = isOkOnly ? ConfirmationMode.OkOnly : ConfirmationMode.YesNoCancel;
                var result = await ConfirmationDialog.ShowConfirmation(this, title, message, mode);
                return result == ConfirmationResult.Yes;
            };

            vm.RequestCaptureAction = (mode) => OpenSnipWindow(mode);

            // Monitor Downloading Status to show/hide separate window
            vm.WhenAnyValue(x => x.IsProcessing)
              .ObserveOn(RxApp.MainThreadScheduler)
              .Subscribe(_ => UpdateDownloadWindow());
        }
    }

    private void OpenSnipWindow(MainWindowViewModel.CaptureMode mode)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var existing = _windowManager.FindWindowOfType<SnipWindow>();

        if (existing != null)
        {
            if (existing.DataContext is SnipWindowViewModel existingVm)
            {
                existingVm.HandleCaptureModeRequest(mode);
            }
            existing.Activate();
            return;
        }

        var snip = new SnipWindow(_screenLayoutService, _windowLayerService);
        
        // Multi-monitor support: Span ALL screens
        var allScreens = snip.Screens.All;
        if (allScreens.Count > 0)
        {
            var screenBounds = allScreens.AsValueEnumerable().Select(s => s.Bounds).ToList();
            var primaryScreen = snip.Screens.Primary ?? allScreens.AsValueEnumerable().First();
            double unifiedScaling = primaryScreen.Scaling;

            if (_screenLayoutService.TryGetUnifiedDesktopPlacement(screenBounds, unifiedScaling, out var position, out var size))
            {
                snip.WindowStartupLocation = WindowStartupLocation.Manual;
                snip.Position = position;
                snip.Width = size.Width;
                snip.Height = size.Height;
            }
        }
        
        var snipVm = new SnipWindowViewModel(
            vm.BorderColor, 
            vm.BorderThickness, 
            vm.MaskOpacity,
            vm.RecordingService,
            vm
        );
        snipVm.AutoActionMode = (int)mode;
        if (mode == MainWindowViewModel.CaptureMode.Record)
        {
            snipVm.CurrentMode = SnipMode.Recording;
        }
        else if (mode == MainWindowViewModel.CaptureMode.Translate)
        {
            snipVm.CurrentMode = SnipMode.Translation;
            snipVm.InitializeTranslationToolbarPosition();
        }
        snip.DataContext = snipVm;
        snip.Show();
    }


    private void UpdateDownloadWindow()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _downloadWindowService.Update(this, vm, vm.IsProcessing);
    }
}