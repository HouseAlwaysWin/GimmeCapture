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
            else if (tb.Name == "CopyHotkeyBox") vm.CopyHotkey = hotkeyStr;
            else if (tb.Name == "PinHotkeyBox") vm.PinHotkey = hotkeyStr;
        }

        e.Handled = true;
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel vm)
        {
            // Initialize Hotkey Service with this Window
            vm.HotkeyService.Initialize(this);

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
                
                // Multi-monitor support: Determine target screen based on cursor position
                POINT p;
                PixelPoint cursorPoint = new PixelPoint(0, 0);
                if (GetCursorPos(out p))
                {
                    cursorPoint = new PixelPoint(p.X, p.Y);
                }
                else if (desktop?.MainWindow != null)
                {
                    cursorPoint = desktop.MainWindow.Position;
                }

                var targetScreen = snip.Screens.ScreenFromPoint(cursorPoint) ?? snip.Screens.All.FirstOrDefault();
                
                if (targetScreen != null)
                {
                    snip.WindowStartupLocation = WindowStartupLocation.Manual;
                    
                    // Position and Size SnipWindow to match target screen's bounds, accounting for DPI scaling
                    double scaling = targetScreen.Scaling;
                    snip.Position = targetScreen.Bounds.TopLeft;
                    snip.Width = targetScreen.Bounds.Width / scaling;
                    snip.Height = targetScreen.Bounds.Height / scaling;
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
              .Subscribe(isProcessing => 
              {
                  // Only show global download window if MainWindow is visible and active
                  // This prevents double UI when using Floating Windows
                  if (isProcessing && this.IsVisible && this.WindowState != WindowState.Minimized)
                  {
                      if (_downloadWindow == null)
                      {
                          try
                          {
                              _downloadWindow = new ResourceDownloadWindow
                              {
                                  DataContext = vm
                              };
                              _downloadWindow.Show(this);
                          }
                          catch (Exception ex)
                          {
                              System.Diagnostics.Debug.WriteLine($"Failed to show download window: {ex}");
                          }
                      }
                  }
                  else
                  {
                      _downloadWindow?.Close();
                      _downloadWindow = null;
                  }
              });
        }
    }

    private ResourceDownloadWindow? _downloadWindow;
}