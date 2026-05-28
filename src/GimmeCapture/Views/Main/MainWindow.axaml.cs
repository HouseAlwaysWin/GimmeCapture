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
using GimmeCapture.Views.Main.Tabs;

namespace GimmeCapture.Views.Main;

public partial class MainWindow : Window
{
    private readonly IDownloadWindowService _downloadWindowService;
    private readonly ISnipWindowFactory _snipWindowFactory;
    private ContentControl? _recordTabHost;
    private ContentControl? _translationTabHost;
    private ContentControl? _modulesTabHost;
    private ContentControl? _aboutTabHost;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    public MainWindow() : this(new MainWindowViewModel(), new NoOpDownloadWindowService(), new NoOpSnipWindowFactory())
    {
    }

    public MainWindow(
        MainWindowViewModel? viewModel,
        IDownloadWindowService? downloadWindowService,
        ISnipWindowFactory? snipWindowFactory)
    {
        _downloadWindowService = downloadWindowService ?? new NoOpDownloadWindowService();
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

        if (DataContext is MainWindowViewModel vm)
        {
            // Initialize Hotkey Service with this Window AFTER it has a handle
            vm.HotkeyService.Initialize(this);
        }
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

            vm.HotkeyService.OnHotkeyRegistrationFailed = (id, hotkey, error) =>
            {
                var hotkeyName = id switch
                {
                    HotkeyIds.Snip => LocalizationService.Instance["StartCapture"] ?? "Screenshot",
                    HotkeyIds.Record => LocalizationService.Instance["CaptureModeRecord"] ?? "Record",
                    HotkeyIds.Translate => LocalizationService.Instance["TranslateHotkey"] ?? "Translate",
                    HotkeyIds.Copy => LocalizationService.Instance["TipCopy"] ?? "Copy",
                    HotkeyIds.Pin => LocalizationService.Instance["TipPin"] ?? "Pin",
                    _ => hotkey
                };

                vm.StatusText = $"[RegisterFailed] {hotkey} -> {hotkeyName}";

                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await ConfirmationDialog.ShowConfirmation(
                        this,
                        "\u5feb\u6377\u9375\u8a3b\u518a\u5931\u6557",
                        $"\u7121\u6cd5\u8a3b\u518a\u300c{hotkeyName}\u300d\u7684\u5feb\u6377\u9375 {hotkey}\u3002\u9019\u500b\u7d44\u5408\u53ef\u80fd\u5df2\u88ab Windows \u6216\u5176\u4ed6\u7a0b\u5f0f\u4f7f\u7528\u3002",
                        ConfirmationMode.OkOnly);
                });
            };

            vm.RequestCaptureAction = OpenSnipWindow;
            vm.GetActiveSnipViewModelAction = ResolveActiveSnipViewModel;
            vm.ShowUpdateDialogAction = async (message, isUpdateAvailable) =>
            {
                return await UpdateDialog.ShowDialog(this, message, isUpdateAvailable);
            };

            // Monitor Downloading Status to show/hide separate window
            vm.WhenAnyValue(x => x.IsProcessing)
              .ObserveOn(RxApp.MainThreadScheduler)
              .Subscribe(_ => UpdateDownloadWindow());
        }
    }

    private SnipWindowViewModel? ResolveActiveSnipViewModel()
    {
        return _snipWindowFactory.GetActiveViewModel() as SnipWindowViewModel;
    }

    private void OpenSnipWindow(CaptureMode mode)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            _snipWindowFactory.Open(vm, mode);
        }
    }


    private void UpdateDownloadWindow()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        _downloadWindowService.Update(this, vm, vm.IsProcessing);
    }

    private void EnsureLazyTabContent(int selectedIndex)
    {
        switch (selectedIndex)
        {
            case 2:
                _recordTabHost ??= this.FindControl<ContentControl>("RecordTabHost");
                _recordTabHost!.Content ??= new SettingsRecordTab();
                break;
            case 3:
                if (DataContext is MainWindowViewModel translationVm)
                {
                    translationVm.RefreshLlamaModelCatalog();
                }

                _translationTabHost ??= this.FindControl<ContentControl>("TranslationTabHost");
                _translationTabHost!.Content ??= new SettingsTranslationTab();
                break;
            case 5:
                if (DataContext is MainWindowViewModel vm)
                {
                    vm.EnsureModulesInitialized();
                }

                _modulesTabHost ??= this.FindControl<ContentControl>("ModulesTabHost");
                _modulesTabHost!.Content ??= new SettingsModulesTab();
                break;
            case 6:
                _aboutTabHost ??= this.FindControl<ContentControl>("AboutTabHost");
                _aboutTabHost!.Content ??= new SettingsAboutTab();
                break;
        }
    }
}
