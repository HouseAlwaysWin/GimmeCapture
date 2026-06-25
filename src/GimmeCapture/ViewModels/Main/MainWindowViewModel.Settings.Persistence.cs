using System;
using Avalonia.Media;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    private static readonly string[] SettingsBackedHotkeyPropertyNames =
    [
        nameof(Snip_Rectangle), nameof(Snip_Ellipse), nameof(Snip_Arrow), nameof(Snip_Line), nameof(Snip_Pen),
        nameof(Snip_Text), nameof(Snip_Mosaic), nameof(Snip_Blur), nameof(Snip_Undo), nameof(Snip_Redo),
        nameof(Snip_Clear), nameof(Snip_Save), nameof(Snip_Copy), nameof(Snip_Pin), nameof(Snip_Close),
        nameof(Snip_Toolbar), nameof(Snip_SelectionMode), nameof(Snip_CropMode), nameof(Snip_RemoveBackground),
        nameof(Snip_FullscreenSelect), nameof(Snip_MagicWand),
        nameof(Record_Rectangle), nameof(Record_Ellipse), nameof(Record_Arrow), nameof(Record_Line), nameof(Record_Pen),
        nameof(Record_Text), nameof(Record_Mosaic), nameof(Record_Blur), nameof(Record_Undo), nameof(Record_Redo),
        nameof(Record_Clear), nameof(Record_Save), nameof(Record_Copy), nameof(Record_Close), nameof(Record_Toolbar),
        nameof(Record_Action), nameof(Record_Playback), nameof(Record_FullscreenSelect),
        nameof(Record_SwitchToSnip), nameof(Record_SwitchToTranslate),
        nameof(Translate_Action), nameof(Translate_Pin), nameof(Translate_Toolbar), nameof(Translate_Close),
        nameof(Translate_TranslateAll), nameof(Translate_ScanAll), nameof(Translate_ClearAll),
        nameof(Translate_ToggleSelect), nameof(Translate_AutoDetect), nameof(Translate_SelectionHoldModifier),
        nameof(Translate_ModeCursor), nameof(Translate_ModeSingle), nameof(Translate_ModeMulti),
        nameof(Translate_SwitchToSnip), nameof(Translate_SwitchToRecord),
        nameof(Snip_SwitchToTranslate), nameof(Snip_SwitchToRecord)
    ];

    private MainWindowSettingsSnapshot CreateSettingsSnapshot()
    {
        return new MainWindowSettingsSnapshot
        {
            Language = SelectedLanguageOption.Value,
            RunOnStartup = RunOnStartup,
            AutoCheckUpdates = AutoCheckUpdates,
            BorderThickness = BorderThickness,
            BorderColor = BorderColor,
            ThemeColor = ThemeColor,
            WingScale = WingScale,
            HideSnipPinDecoration = HideSnipPinDecoration,
            HideSnipPinBorder = HideSnipPinBorder,
            HideSnipSelectionDecoration = HideSnipSelectionDecoration,
            HideSnipSelectionBorder = HideSnipSelectionBorder,
            AutoPinScreenshotSelection = AutoPinScreenshotSelection,
            CaptureDelay = CaptureDelay,
            OcrTextLayout = OcrTextLayout,
            ScrollingCaptureDirection = ScrollingCaptureDirection,
            HideRecordPinDecoration = HideRecordPinDecoration,
            HideRecordPinBorder = HideRecordPinBorder,
            HideRecordSelectionDecoration = HideRecordSelectionDecoration,
            HideRecordSelectionBorder = HideRecordSelectionBorder,
            DefaultHideSnipToolbar = DefaultHideSnipToolbar,
            DefaultHideRecordToolbar = DefaultHideRecordToolbar,
            AutoSave = AutoSave,
            EnableHistory = EnableHistory,
            RevealAfterSave = RevealAfterSave,
            SaveDirectory = SaveDirectory,
            ShowSnipCursor = ShowSnipCursor,
            ShowRecordCursor = ShowRecordCursor,
            RecordSystemAudio = RecordSystemAudio,
            VideoSaveDirectory = RecordingSettings.VideoSaveDirectory,
            RecordFormat = RecordingSettings.RecordFormat,
            VideoCodec = RecordingSettings.VideoCodec,
            VideoEncoderHint = RecordingSettings.VideoEncoderHint,
            VideoQuality = RecordingSettings.VideoQuality,
            RecordFps = RecordingSettings.RecordFPS,
            MaxRecordingSizeMb = RecordingSettings.MaxRecordingSizeMB,
            PlaybackUiFps = PlaybackUiFps,
            PlaybackTimelineFps = PlaybackTimelineFps,
            UseFixedRecordPath = RecordingSettings.UseFixedRecordPath,
            TempDirectory = TempDirectory,
            SnipHotkey = SnipHotkey,
            RecordHotkey = RecordHotkey,
            TranslateHotkey = TranslateHotkey,
            TextCopyHotkey = TextCopyHotkey,
            ScrollingCaptureHotkey = ScrollingCaptureHotkey,
            AIResourcesDirectory = AIResourcesDirectory,
            EnableAI = EnableAI,
            ShowAIScanBox = ShowAIScanBox,
            EnableAIScan = EnableAIScan,
            SourceLanguage = SourceLanguage,
            TargetLanguage = TargetLanguage,
            SelectedTranslationEngine = SelectedTranslationEngine,
            LlamaModelId = LlamaModelId,
            LlamaCustomModelPath = LlamaCustomModelPath,
            LlamaContextSize = LlamaContextSize,
            LlamaGpuLayers = LlamaGpuLayers
        };
    }

    private void ApplySettingsSnapshot(MainWindowSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        RunOnStartup = snapshot.RunOnStartup;
        AutoCheckUpdates = snapshot.AutoCheckUpdates;
        BorderThickness = snapshot.BorderThickness;
        AutoSave = snapshot.AutoSave;
        EnableHistory = snapshot.EnableHistory;
        RevealAfterSave = snapshot.RevealAfterSave;
        SaveDirectory = snapshot.SaveDirectory;
        SnipHotkey = snapshot.SnipHotkey;
        TranslateHotkey = snapshot.TranslateHotkey;
        RecordHotkey = snapshot.RecordHotkey;
        TextCopyHotkey = snapshot.TextCopyHotkey;
        ScrollingCaptureHotkey = snapshot.ScrollingCaptureHotkey;
        RecordingSettings.RecordFormat = snapshot.RecordFormat;
        RecordingSettings.VideoSaveDirectory = snapshot.VideoSaveDirectory;
        RecordingSettings.VideoCodec = snapshot.VideoCodec;
        RecordingSettings.VideoEncoderHint = snapshot.VideoEncoderHint;
        RecordingSettings.VideoQuality = snapshot.VideoQuality;
        RecordingSettings.UseFixedRecordPath = snapshot.UseFixedRecordPath;
        HideSnipPinDecoration = snapshot.HideSnipPinDecoration;
        HideSnipPinBorder = snapshot.HideSnipPinBorder;
        DefaultHideSnipToolbar = snapshot.DefaultHideSnipToolbar;
        DefaultHideRecordToolbar = snapshot.DefaultHideRecordToolbar;
        HideRecordPinDecoration = snapshot.HideRecordPinDecoration;
        HideRecordPinBorder = snapshot.HideRecordPinBorder;
        HideSnipSelectionDecoration = snapshot.HideSnipSelectionDecoration;
        HideSnipSelectionBorder = snapshot.HideSnipSelectionBorder;
        AutoPinScreenshotSelection = snapshot.AutoPinScreenshotSelection;
        CaptureDelay = snapshot.CaptureDelay;
        OcrTextLayout = snapshot.OcrTextLayout;
        ScrollingCaptureDirection = snapshot.ScrollingCaptureDirection;
        HideRecordSelectionDecoration = snapshot.HideRecordSelectionDecoration;
        HideRecordSelectionBorder = snapshot.HideRecordSelectionBorder;
        ShowSnipCursor = snapshot.ShowSnipCursor;
        ShowRecordCursor = snapshot.ShowRecordCursor;
        RecordSystemAudio = snapshot.RecordSystemAudio;
        PlaybackUiFps = snapshot.PlaybackUiFps;
        PlaybackTimelineFps = snapshot.PlaybackTimelineFps;
        TempDirectory = snapshot.TempDirectory;
        ShowAIScanBox = snapshot.ShowAIScanBox;
        EnableAI = snapshot.EnableAI;
        WingScale = snapshot.WingScale;
        RecordingSettings.RecordFPS = snapshot.RecordFps;
        RecordingSettings.MaxRecordingSizeMB = snapshot.MaxRecordingSizeMb;
        EnableAIScan = snapshot.EnableAIScan;
        AIResourcesDirectory = snapshot.AIResourcesDirectory;
        SelectedTranslationEngine = snapshot.SelectedTranslationEngine;
        LlamaModelId = snapshot.LlamaModelId;
        LlamaCustomModelPath = snapshot.LlamaCustomModelPath;
        LlamaContextSize = snapshot.LlamaContextSize;
        LlamaGpuLayers = snapshot.LlamaGpuLayers;
        SourceLanguage = snapshot.SourceLanguage;
        TargetLanguage = snapshot.TargetLanguage;
        BorderColor = snapshot.BorderColor;
        ThemeColor = snapshot.ThemeColor;
        SelectedLanguageOption = AvailableLanguages.AsValueEnumerable().FirstOrDefault(x => x.Value == snapshot.Language) ?? AvailableLanguages[0];
        RecordingSettings.SelectedVideoCodecOption = RecordingSettings.VideoCodecOptions.AsValueEnumerable().FirstOrDefault(x => x.Value == snapshot.VideoCodec);
        RecordingSettings.SelectedVideoQualityOption = RecordingSettings.VideoQualityOptions.AsValueEnumerable().FirstOrDefault(x => x.Value == snapshot.VideoQuality);
        RecordingSettings.SelectedVideoEncoderHintOption = RecordingSettings.VideoEncoderHintOptions.AsValueEnumerable().FirstOrDefault(x => x.Value == snapshot.VideoEncoderHint);
    }

    private void RaiseSettingsBackedPropertyNotifications()
    {
        foreach (var propertyName in SettingsBackedHotkeyPropertyNames)
        {
            this.RaisePropertyChanged(propertyName);
        }

        this.RaisePropertyChanged(nameof(SourceLanguage));
        this.RaisePropertyChanged(nameof(TargetLanguage));
        this.RaisePropertyChanged(nameof(AIResourcesDirectory));
        this.RaisePropertyChanged(nameof(ShowAIScanBox));
        this.RaisePropertyChanged(nameof(EnableAIScan));
        this.RaisePropertyChanged(nameof(EnableOcrSelectionDetection));
        this.RaisePropertyChanged(nameof(AvailableCaptureDelays));
        this.RaisePropertyChanged(nameof(AvailableOcrTextLayouts));
        this.RaisePropertyChanged(nameof(AvailableScrollDirections));
        this.RaisePropertyChanged(nameof(ScrollingCaptureDirection));
    }
}
