using ReactiveUI;
using Avalonia;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Views.Dialogs;
using GimmeCapture.Services.Core;
using Avalonia.Controls.ApplicationLifetimes;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private async Task StartCapture(CaptureMode mode = CaptureMode.Normal)
    {
        if (mode == CaptureMode.Record)
        {
            if (!FfmpegDownloader.IsFFmpegAvailable())
            {
                var msg = LocalizationService.Instance["FFmpegDownloadConfirm"] ?? "FFmpeg is required for recording. Download now?";
                bool confirmed = false;
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                    if (mainWindow != null)
                    {
                        confirmed = await UpdateDialog.ShowDialog(mainWindow, msg, isUpdateAvailable: true);
                    }
                });

                if (!confirmed) return;
                await FfmpegDownloader.EnsureFFmpegAsync();
                if (!FfmpegDownloader.IsFFmpegAvailable()) return;
            }
        }

        await SaveSettingsAsync();
        RequestCaptureAction?.Invoke(mode);
        SetStatus("StatusSnip");
    }
    
    private async Task SaveAndClose()
    {
        await SaveSettingsAsync();
        SetStatus("StatusSaved");
    }

    private async Task ResetToDefault()
    {
        _isDataLoading = true;
        var defaultSettings = new AppSettings();
        
        RunOnStartup = defaultSettings.RunOnStartup;
        AutoCheckUpdates = defaultSettings.AutoCheckUpdates;
        BorderThickness = defaultSettings.BorderThickness;
        MaskOpacity = defaultSettings.MaskOpacity;
        AutoSave = defaultSettings.AutoSave;
        SnipHotkey = defaultSettings.SnipHotkey;
        CopyHotkey = defaultSettings.CopyHotkey;
        PinHotkey = defaultSettings.PinHotkey;
        RecordHotkey = defaultSettings.RecordHotkey;
        RecordFormat = defaultSettings.RecordFormat;
        VideoCodec = defaultSettings.VideoCodec;
        
        RectangleHotkey = defaultSettings.RectangleHotkey;
        EllipseHotkey = defaultSettings.EllipseHotkey;
        ArrowHotkey = defaultSettings.ArrowHotkey;
        LineHotkey = defaultSettings.LineHotkey;
        PenHotkey = defaultSettings.PenHotkey;
        TextHotkey = defaultSettings.TextHotkey;
        MosaicHotkey = defaultSettings.MosaicHotkey;
        BlurHotkey = defaultSettings.BlurHotkey;
        
        UndoHotkey = defaultSettings.UndoHotkey;
        RedoHotkey = defaultSettings.RedoHotkey;
        ClearHotkey = defaultSettings.ClearHotkey;
        SaveHotkey = defaultSettings.SaveHotkey;
        CloseHotkey = defaultSettings.CloseHotkey;
        TogglePlaybackHotkey = defaultSettings.TogglePlaybackHotkey;
        ToggleToolbarHotkey = defaultSettings.ToggleToolbarHotkey;
        SelectionModeHotkey = defaultSettings.SelectionModeHotkey;
        CropModeHotkey = defaultSettings.CropModeHotkey;
        HideSnipPinDecoration = false;
        HideSnipPinBorder = false;
        HideRecordPinDecoration = false;
        HideRecordPinBorder = false;
        HideSnipSelectionDecoration = false;
        HideSnipSelectionBorder = false;
        HideRecordSelectionDecoration = false;
        HideRecordSelectionBorder = false;
        ShowSnipCursor = defaultSettings.ShowSnipCursor;
        ShowRecordCursor = defaultSettings.ShowRecordCursor;
        TempDirectory = defaultSettings.TempDirectory;
        
        if (Color.TryParse(defaultSettings.BorderColorHex, out var color))
            BorderColor = color;
            
        if (Color.TryParse(defaultSettings.ThemeColorHex, out var themeColor))
            ThemeColor = themeColor;

        _isDataLoading = false;
        SetStatus("StatusReset");
        IsModified = false;
        await SaveSettingsAsync();
    }

    public async Task CheckForUpdates() => await CheckForUpdates(false);

    private async Task CheckForUpdates(bool silent)
    {
        if (!silent) SetStatus("CheckingUpdate");
        var release = await UpdateService.CheckForUpdateAsync();
        
        if (release != null)
        {
            SetStatus("StatusReady");
            var msg = string.Format(LocalizationService.Instance["UpdateFound"], release.TagName);
            
            bool? result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow == null) return false;
                return await UpdateDialog.ShowDialog(mainWindow, msg, isUpdateAvailable: true);
            });

            if (result == true)
            {
                var zipPath = await UpdateService.DownloadUpdateAsync(release);
                if (!string.IsNullOrEmpty(zipPath))
                {
                    var readyMsg = LocalizationService.Instance["UpdateReady"];
                    bool? readyResult = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                        if (mainWindow == null) return false;
                        return await UpdateDialog.ShowDialog(mainWindow, readyMsg, isUpdateAvailable: true);
                    });

                    if (readyResult == true)
                    {
                        UpdateService.ApplyUpdate(zipPath);
                    }
                }
            }
        }
        else
        {
            if (!silent)
            {
                SetStatus("StatusReady");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => {
                    var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                    if (mainWindow != null) await UpdateDialog.ShowDialog(mainWindow, LocalizationService.Instance["NoUpdateFound"], isUpdateAvailable: false);
                });
            }
        }
    }

    public async Task SelectVideoPath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) VideoSaveDirectory = path;
        }
    }

    public async Task SelectSavePath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) SaveDirectory = path;
        }
    }

    public async Task SelectTempPath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) TempDirectory = path;
        }
    }

    private void OpenProjectUrl()
    {
        try
        {
            var url = "https://github.com/HouseAlwaysWin/GimmeCapture";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open project URL: {ex.Message}");
        }
    }
}
