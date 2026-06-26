using ReactiveUI;
using Avalonia;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private async Task StartCapture(CaptureMode mode = CaptureMode.Normal)
    {
        if (mode == CaptureMode.Record)
        {
            if (!FfmpegDownloader.IsFFmpegAvailable())
            {
                // FFmpeg toolkit is bundled with the app. If unavailable, this installation is incomplete.
                SetStatus("FFmpegNotReady");
                return;
            }
        }

        await SaveSettingsAsync();
        RequestCaptureAction?.Invoke(mode);
        SetStatus("StatusSnip");
    }

    internal async Task<CaptureLaunchResult> RunCaptureActionAsync(
        CaptureMode mode,
        Func<Task> captureAsync)
    {
        ArgumentNullException.ThrowIfNull(captureAsync);

        CaptureLaunchResult? launchResult = null;
        try
        {
            launchResult = await _captureLaunchCoordinator.TryLaunchAsync(
                mode,
                CaptureDelay,
                async (remaining, cancellationToken) =>
                {
                    if (ShowCaptureCountdownAction != null)
                    {
                        await ShowCaptureCountdownAction(remaining, cancellationToken);
                    }
                },
                async () =>
                {
                    CloseCaptureCountdownAction?.Invoke();
                    await captureAsync();
                });

            if (launchResult == CaptureLaunchResult.Cancelled)
            {
                SetStatus("CaptureDelayCancelled");
            }
        }
        finally
        {
            if (launchResult != CaptureLaunchResult.AlreadyRunning)
            {
                CloseCaptureCountdownAction?.Invoke();
            }
        }

        return launchResult ?? CaptureLaunchResult.Cancelled;
    }
    
    private async Task ResetToDefault()
    {
        _isDataLoading = true;
        
        // Reset AppSettings directly
        var defaultSettings = new AppSettings();
        _settingsService.UpdateSettings(defaultSettings);
        
        // Populate ViewModel properties from the newly reset settings
        RunOnStartup = defaultSettings.RunOnStartup;
        AutoCheckUpdates = defaultSettings.AutoCheckUpdates;
        BorderThickness = defaultSettings.BorderThickness;
        AutoSave = defaultSettings.AutoSave;
        SnipHotkey = defaultSettings.SnipHotkey;
        TranslateHotkey = defaultSettings.TranslateHotkey;
        RecordHotkey = defaultSettings.RecordHotkey;
        TextCopyHotkey = defaultSettings.TextCopyHotkey;
        ScrollingCaptureHotkey = defaultSettings.ScrollingCaptureHotkey;
        RecordingSettings.RecordFormat = defaultSettings.RecordFormat;
        RecordingSettings.VideoCodec = defaultSettings.VideoCodec;
        RecordingSettings.VideoEncoderHint = defaultSettings.VideoEncoderHint;
        RecordingSettings.SelectedVideoEncoderHintOption = RecordingSettings.VideoEncoderHintOptions.AsValueEnumerable().FirstOrDefault(x => x.Value == defaultSettings.VideoEncoderHint);
        
        // Refresh all hotkey properties (they access Settings directly now)
        var hotkeyProps = new[] {
            nameof(Snip_Rectangle), nameof(Snip_Ellipse), nameof(Snip_Arrow), nameof(Snip_Line), nameof(Snip_Pen),
            nameof(Snip_Text), nameof(Snip_Mosaic), nameof(Snip_Blur), nameof(Snip_Undo), nameof(Snip_Redo),
            nameof(Snip_Clear), nameof(Snip_Save), nameof(Snip_Copy), nameof(Snip_Pin), nameof(Snip_Close),
                nameof(Snip_Toolbar), nameof(Snip_SelectionMode), nameof(Snip_CropMode), nameof(Snip_RemoveBackground), nameof(Snip_MagicWand), nameof(Snip_FullscreenSelect),
            nameof(Record_Rectangle), nameof(Record_Ellipse), nameof(Record_Arrow), nameof(Record_Line), nameof(Record_Pen),
            nameof(Record_Text), nameof(Record_Mosaic), nameof(Record_Blur), nameof(Record_Undo), nameof(Record_Redo),
            nameof(Record_Clear), nameof(Record_Save), nameof(Record_Copy), nameof(Record_Close), nameof(Record_Toolbar),
            nameof(Record_Action), nameof(Record_Playback), nameof(Record_Stop), nameof(Record_FullscreenSelect),
            nameof(Record_SwitchToSnip), nameof(Record_SwitchToTranslate),
            nameof(Translate_Action), nameof(Translate_Pin), nameof(Translate_Toolbar), nameof(Translate_Close),
            nameof(Translate_TranslateAll), nameof(Translate_ScanAll), nameof(Translate_ClearAll),
            nameof(Translate_ToggleSelect), nameof(Translate_AutoDetect),
            nameof(Translate_SelectionHoldModifier),
            nameof(Translate_ModeCursor), nameof(Translate_ModeSingle), nameof(Translate_ModeMulti),
            nameof(Translate_SwitchToSnip), nameof(Translate_SwitchToRecord),
            nameof(Snip_SwitchToTranslate), nameof(Snip_SwitchToRecord)
        };
        foreach (var prop in hotkeyProps) this.RaisePropertyChanged(prop);

        HideSnipPinDecoration = false;
        HideSnipPinBorder = false;
        HideRecordPinDecoration = false;
        HideRecordPinBorder = false;
        HideSnipSelectionDecoration = false;
        HideSnipSelectionBorder = false;
        AutoPinScreenshotSelection = defaultSettings.AutoPinScreenshotSelection;
        CaptureDelay = defaultSettings.CaptureDelay;
        OcrTextLayout = defaultSettings.OcrTextLayout;
        ScrollingCaptureDirection = defaultSettings.ScrollingCaptureDirection;
        HideRecordSelectionDecoration = false;
        HideRecordSelectionBorder = false;
        ShowSnipCursor = defaultSettings.ShowSnipCursor;
        ShowRecordCursor = defaultSettings.ShowRecordCursor;
        RecordSystemAudio = defaultSettings.RecordSystemAudio;
        EnableWebcam = defaultSettings.EnableWebcam;
        WebcamDeviceName = defaultSettings.WebcamDeviceName;
        WebcamCorner = defaultSettings.WebcamCorner;
        RecordMicrophone = defaultSettings.RecordMicrophone;
        SelectedMicDeviceId = defaultSettings.SelectedMicDeviceId;
        MicVolume = defaultSettings.MicVolume;
        HighlightCursor = defaultSettings.HighlightCursor;
        HighlightClicks = defaultSettings.HighlightClicks;
        ShowKeystrokes = defaultSettings.ShowKeystrokes;
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
            
            // Check if already downloaded
            string? zipPath = UpdateService.IsUpdateDownloaded(release) ? UpdateService.DownloadedZipPath : null;

            if (string.IsNullOrEmpty(zipPath))
            {
                var msg = string.Format(LocalizationService.Instance["UpdateFound"], release.TagName);
                bool? result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var mainWindow = _windowManager.GetMainWindow();
                    if (mainWindow == null || ShowUpdateDialogAction == null) return false;
                    return await ShowUpdateDialogAction(msg, true);
                });

                if (result == true)
                {
                    zipPath = await UpdateService.DownloadUpdateAsync(release);
                }
            }

            if (!string.IsNullOrEmpty(zipPath))
            {
                var readyMsg = LocalizationService.Instance["UpdateReady"];
                bool? readyResult = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var mainWindow = _windowManager.GetMainWindow();
                    if (mainWindow == null || ShowUpdateDialogAction == null) return false;

                    // Ensure window is visible and focused
                    if (mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
                    {
                        mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                    }
                    mainWindow.Activate();
                    mainWindow.Show();

                    return await ShowUpdateDialogAction(readyMsg, true);
                });

                if (readyResult == true)
                {
                    UpdateService.ApplyUpdate(zipPath, release.NormalizedVersion);
                }
            }
        }
        else
        {
            if (!silent)
            {
                SetStatus("StatusReady");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => {
                    if (ShowUpdateDialogAction != null)
                    {
                        await ShowUpdateDialogAction(LocalizationService.Instance["NoUpdateFound"], false);
                    }
                });
            }
        }
    }

    public async Task SelectVideoPath()
    {
        if (PickFolderAction != null)
        {
            var path = await PickFolderAction();
            if (!string.IsNullOrEmpty(path)) RecordingSettings.VideoSaveDirectory = path;
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
