using System;
using Avalonia.Controls;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Views.Main;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Media;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using ReactiveUI;
using System.Reactive.Linq;

namespace GimmeCapture.Views.Main;

public partial class MainWindow : Window
{
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

    private bool _isClosingFromDialog = false;
    private bool _isExiting = false;

    public void Shutdown()
    {
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
        
        // 如果有修改，仍可以在後台提示
        if (DataContext is MainWindowViewModel vm && vm.IsModified)
        {
             // 可以在這裡非同步保存，或者單純 Hide
             // 為了避免警告且符合行為需求，這裡移除語意不明的註解
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

            vm.ConfirmAction = async (title, message) =>
            {
                var result = await ConfirmationDialog.ShowConfirmation(this, title, message);
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

        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var existing = desktop?.Windows.OfType<SnipWindow>().FirstOrDefault();

        if (existing != null)
        {
            if (existing.DataContext is SnipWindowViewModel existingVm)
            {
                existingVm.AutoActionMode = (int)mode;
            }
            existing.Activate();
            return;
        }

        var snip = new SnipWindow();
        
        // Multi-monitor support: Span ALL screens
        var allScreens = snip.Screens.All;
        if (allScreens.Count > 0)
        {
            int physMinX = allScreens.Min(s => s.Bounds.X);
            int physMinY = allScreens.Min(s => s.Bounds.Y);
            int physMaxR = allScreens.Max(s => s.Bounds.Right);
            int physMaxB = allScreens.Max(s => s.Bounds.Bottom);

            snip.WindowStartupLocation = WindowStartupLocation.Manual;
            snip.Position = new PixelPoint(physMinX, physMinY);
            
            var primaryScreen = snip.Screens.Primary ?? allScreens.First();
            double unifiedScaling = primaryScreen.Scaling;
            
            snip.Width = (physMaxR - physMinX) / unifiedScaling;
            snip.Height = (physMaxB - physMinY) / unifiedScaling;
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
            snipVm.IsRecordingMode = true;
        }
        else if (mode == MainWindowViewModel.CaptureMode.Translate)
        {
            snipVm.IsTranslationMode = true;
            snipVm.InitializeTranslationToolbarPosition();
        }
        snip.DataContext = snipVm;
        snip.Show();
    }


    private void UpdateDownloadWindow()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // 僅在主視窗縮小或隱藏時且正在處理中，才顯示全域懸浮下載視窗
        // 現在縮小時視窗仍可見（在工作列），所以判斷 Minimized 
        bool isBackground = this.WindowState == WindowState.Minimized || !this.IsVisible;

        if (vm.IsProcessing && isBackground)
        {
            if (_downloadWindow == null)
            {
                try 
                {
                    _downloadWindow = new ResourceDownloadWindow
                    {
                        DataContext = vm
                    };

                    if (this.IsVisible)
                    {
                        _downloadWindow.Show(this);
                    }
                    else
                    {
                        // 如果主視窗隱藏中（如在系統匣），不能設定為 Owner 否則會拋出 InvalidOperationException
                        _downloadWindow.Show();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to show download window: {ex}");
                }
            }
            else
            {
                _downloadWindow.Show();
                _downloadWindow.WindowState = WindowState.Normal;
                _downloadWindow.Activate();
            }
        }
        else
        {
            // 如果主視窗已打開或是處理已完成，則隱藏/關閉懸浮視窗
            if (!vm.IsProcessing)
            {
                if (_downloadWindow != null)
                {
                     _downloadWindow.Close();
                     _downloadWindow = null;
                }
            }
            else
            {
                _downloadWindow?.Hide();
            }
        }
    }

    private ResourceDownloadWindow? _downloadWindow;
}