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
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosingFromDialog) return;

        if (DataContext is MainWindowViewModel vm && vm.IsModified)
        {
            e.Cancel = true;
            var result = await ConfirmationDialog.ShowConfirmation(this);
            
            if (result == ConfirmationResult.Yes)
            {
                var success = await vm.SaveSettingsAsync();
                if (success)
                {
                    _isClosingFromDialog = true;
                    Close();
                }
                else
                {
                    // If save failed, stay open and show error
                     var msg = LocalizationService.Instance["SaveFailed"];
                     await UpdateDialog.ShowDialog(this, msg, isUpdateAvailable: false);
                }
            }
            else if (result == ConfirmationResult.No)
            {
                _isClosingFromDialog = true;
                Close();
            }
            // If Cancel, do nothing (window stays open)
        }
    }

    private void OnPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            if (WindowState == WindowState.Minimized)
            {
                // Minimize to Tray
                Hide();
            }
        }
        
        if (e.Property == Window.WindowStateProperty || e.Property == Window.IsVisibleProperty || e.Property == Window.BoundsProperty)
        {
            UpdateDownloadWindow();
        }
    }

    private void HotkeyTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Don't Record purely modifier keys
        var key = e.Key;
        if (key == Avalonia.Input.Key.LeftCtrl || key == Avalonia.Input.Key.RightCtrl ||
            key == Avalonia.Input.Key.LeftAlt || key == Avalonia.Input.Key.RightAlt ||
            key == Avalonia.Input.Key.LeftShift || key == Avalonia.Input.Key.RightShift ||
            key == Avalonia.Input.Key.LWin || key == Avalonia.Input.Key.RWin)
        {
            return;
        }

        var modifiers = e.KeyModifiers;
        var hotkeyStr = "";

        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) hotkeyStr += "Ctrl+";
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) hotkeyStr += "Alt+";
        if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) hotkeyStr += "Shift+";

        hotkeyStr += key.ToString();

        if (DataContext is MainWindowViewModel vm && sender is TextBox tb)
        {
            if (tb.Name == "SnipHotkeyBox") vm.SnipHotkey = hotkeyStr;
            else if (tb.Name == "RecordHotkeyBox") vm.RecordHotkey = hotkeyStr;
            else if (tb.Name == "CopyHotkeyBox_Snip" || tb.Name == "CopyHotkeyBox_Recording") vm.CopyHotkey = hotkeyStr;
            else if (tb.Name == "PinHotkeyBox_Snip" || tb.Name == "PinHotkeyBox_Record" || tb.Name == "PinHotkeyBox_Recording") vm.PinHotkey = hotkeyStr;
            
            // Drawing Tools
            else if (tb.Name == "RectHotkeyBox") vm.RectangleHotkey = hotkeyStr;
            else if (tb.Name == "EllipseHotkeyBox") vm.EllipseHotkey = hotkeyStr;
            else if (tb.Name == "ArrowHotkeyBox") vm.ArrowHotkey = hotkeyStr;
            else if (tb.Name == "LineHotkeyBox") vm.LineHotkey = hotkeyStr;
            else if (tb.Name == "PenHotkeyBox") vm.PenHotkey = hotkeyStr;
            else if (tb.Name == "TextHotkeyBox") vm.TextHotkey = hotkeyStr;
            else if (tb.Name == "MosaicHotkeyBox") vm.MosaicHotkey = hotkeyStr;
            else if (tb.Name == "BlurHotkeyBox") vm.BlurHotkey = hotkeyStr;
            
            // Actions
            else if (tb.Name == "UndoHotkeyBox") vm.UndoHotkey = hotkeyStr;
            else if (tb.Name == "RedoHotkeyBox") vm.RedoHotkey = hotkeyStr;
            else if (tb.Name == "ClearHotkeyBox") vm.ClearHotkey = hotkeyStr;
            else if (tb.Name == "SaveHotkeyBox") vm.SaveHotkey = hotkeyStr;
            else if (tb.Name == "CloseHotkeyBox") vm.CloseHotkey = hotkeyStr;
            else if (tb.Name == "PlaybackHotkeyBox") vm.TogglePlaybackHotkey = hotkeyStr;
            else if (tb.Name == "ToolbarHotkeyBox") vm.ToggleToolbarHotkey = hotkeyStr;
            else if (tb.Name == "SelectionModeHotkeyBox") vm.SelectionModeHotkey = hotkeyStr;
            else if (tb.Name == "CropModeHotkeyBox") vm.CropModeHotkey = hotkeyStr;
        }

        e.Handled = true;
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

            vm.RequestCaptureAction = (mode) =>
            {
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
                        // Calculate the union of all screen bounds in PHYSICAL pixels
                        int physMinX = allScreens.Min(s => s.Bounds.X);
                        int physMinY = allScreens.Min(s => s.Bounds.Y);
                        int physMaxR = allScreens.Max(s => s.Bounds.Right);
                        int physMaxB = allScreens.Max(s => s.Bounds.Bottom);

                        snip.WindowStartupLocation = WindowStartupLocation.Manual;
                        snip.Position = new PixelPoint(physMinX, physMinY);
                        
                        // We use the primary screen's scaling for the entire spanning window.
                        // This is consistent with how Avalonia handles coordinates within a single window.
                        var primaryScreen = snip.Screens.Primary ?? allScreens.First();
                        double unifiedScaling = primaryScreen.Scaling;
                        
                        // Set logical width/height to cover the entire physical range
                        snip.Width = (physMaxR - physMinX) / unifiedScaling;
                        snip.Height = (physMaxB - physMinY) / unifiedScaling;

                        Console.WriteLine($"[MainWindow] SnipWindow spanning: {snip.Position} size: {snip.Width}x{snip.Height} (Scaling: {unifiedScaling})");
                    }
                
                var snipVm = new SnipWindowViewModel(
                    vm?.BorderColor ?? Color.Parse("#E60012"), 
                    vm?.BorderThickness ?? 2, 
                    vm?.MaskOpacity ?? 0.5,
                    vm?.RecordingService,
                    vm
                );
                snipVm.AutoActionMode = (int)mode;
                if (mode == MainWindowViewModel.CaptureMode.Record)
                {
                    snipVm.IsRecordingMode = true;
                }
                snip.DataContext = snipVm;
                snip.Show();
            };

            // Monitor Downloading Status to show/hide separate window
            vm.WhenAnyValue(x => x.IsProcessing)
              .ObserveOn(RxApp.MainThreadScheduler)
              .Subscribe(_ => UpdateDownloadWindow());
        }
    }

    private void UpdateDownloadWindow()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        
        // Show when minimized, hidden, or "shrunk" (small size)
        bool isMinimized = this.WindowState == WindowState.Minimized || !this.IsVisible;
        bool isShrunk = this.Bounds.Width < 500 || this.Bounds.Height < 400;
        bool isBackground = isMinimized || isShrunk;

        if (vm.IsProcessing)
        {
            // If main window is visible and large, check if we're on a download tab
            bool showingDownloadScreen = false;
            if (!isBackground)
            {
                var tabControl = this.FindControl<TabControl>("MainTabControl");
                if (tabControl != null)
                {
                    // Index 4: Modules, Index 5: About
                    showingDownloadScreen = tabControl.SelectedIndex == 4 || tabControl.SelectedIndex == 5;
                }
            }

            if (isBackground || !showingDownloadScreen)
            {
                if (_downloadWindow == null)
                {
                    try 
                    {
                        _downloadWindow = new ResourceDownloadWindow
                        {
                            DataContext = vm
                        };
                        _downloadWindow.Show(); 
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
                _downloadWindow?.Hide();
            }
        }
        else
        {
            _downloadWindow?.Close();
            _downloadWindow = null;
        }
    }

    private ResourceDownloadWindow? _downloadWindow;
}