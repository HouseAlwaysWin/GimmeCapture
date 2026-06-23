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

// ViewModel action wiring (dialogs / pickers / capture / countdown) for MainWindow.
// Split out of MainWindow.axaml.cs by concern — no behavior change.
public partial class MainWindow
{
    private CaptureCountdownWindow? _captureCountdownWindow;

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

            vm.ConfirmYesNoAction = async (title, message) =>
            {
                var result = await ConfirmationDialog.ShowConfirmation(this, title, message, ConfirmationMode.YesNo);
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
                    HotkeyIds.TextCopy => LocalizationService.Instance["TextCopyHotkey"],
                    _ => hotkey
                };

                vm.StatusText = $"[RegisterFailed] {hotkey} -> {hotkeyName}";

                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await ConfirmationDialog.ShowConfirmation(
                        this,
                        "快捷鍵註冊失敗",
                        $"無法註冊「{hotkeyName}」的快捷鍵 {hotkey}。這個組合可能已被 Windows 或其他程式使用。",
                        ConfirmationMode.OkOnly);
                });
            };

            vm.RequestCaptureAction = OpenSnipWindow;
            vm.ShowCaptureCountdownAction = ShowCaptureCountdownAsync;
            vm.CloseCaptureCountdownAction = CloseCaptureCountdown;
            vm.RequestOpenModulesAction = OpenModulesTab;
            vm.GetActiveSnipViewModelAction = ResolveActiveSnipViewModel;
            vm.ShowUpdateDialogAction = async (message, isUpdateAvailable) =>
            {
                return await UpdateDialog.ShowDialog(this, message, isUpdateAvailable);
            };

            // Monitor Downloading Status to show/hide separate window
            vm.WhenAnyValue(x => x.IsProcessing)
              .ObserveOn(RxSchedulers.MainThreadScheduler)
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

    private Task ShowCaptureCountdownAsync(int seconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (DataContext is not MainWindowViewModel vm)
        {
            return Task.CompletedTask;
        }

        _captureCountdownWindow ??= new CaptureCountdownWindow(vm.CancelCaptureCountdown);
        _captureCountdownWindow.UpdateCountdown(seconds);
        if (!_captureCountdownWindow.IsVisible)
        {
            _captureCountdownWindow.Show();
        }

        return Task.CompletedTask;
    }

    private void CloseCaptureCountdown()
    {
        if (_captureCountdownWindow == null)
        {
            return;
        }

        _captureCountdownWindow.Close();
        _captureCountdownWindow = null;
    }
}
