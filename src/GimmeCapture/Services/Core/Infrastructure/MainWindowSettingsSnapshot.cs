using System;
using Avalonia.Media;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed class MainWindowSettingsSnapshot
{
    public required Language Language { get; init; }
    public required bool RunOnStartup { get; init; }
    public required bool AutoCheckUpdates { get; init; }
    public required double BorderThickness { get; init; }
    public required Color BorderColor { get; init; }
    public required Color ThemeColor { get; init; }
    public required double WingScale { get; init; }
    public required bool HideSnipPinDecoration { get; init; }
    public required bool HideSnipPinBorder { get; init; }
    public required bool HideSnipSelectionDecoration { get; init; }
    public required bool CaptureWithoutStealingFocus { get; init; }
    public required bool FreezeScreenOnScreenshot { get; init; }
    public required CaptureDelay CaptureDelay { get; init; }
    public required OcrTextLayout OcrTextLayout { get; init; }
    public required bool SaveOcrTextToFile { get; init; }
    public required ScrollingCaptureDirection ScrollingCaptureDirection { get; init; }
    public required SnipToolbarPosition SnipToolbarPosition { get; init; }
    public required bool HideRecordPinDecoration { get; init; }
    public required bool HideRecordPinBorder { get; init; }
    public required bool HideRecordSelectionDecoration { get; init; }
    public required bool DefaultHideSnipToolbar { get; init; }
    public required bool DefaultHideRecordToolbar { get; init; }
    public required bool AutoSave { get; init; }
    public required bool EnableHistory { get; init; }
    public required bool RevealAfterSave { get; init; }
    public required string SaveDirectory { get; init; }
    public required string FileNameTemplate { get; init; }
    public required string ImgurClientId { get; init; }
    public required bool ShowSnipCursor { get; init; }
    public required bool ShowRecordCursor { get; init; }
    public required bool RecordSystemAudio { get; init; }
    public required bool EnableWebcam { get; init; }
    public required string WebcamDeviceName { get; init; }
    public required int WebcamCorner { get; init; }
    public int WebcamSize { get; init; } = 1;
    public bool WebcamCircular { get; init; }
    public required bool RecordMicrophone { get; init; }
    public required string SelectedMicDeviceId { get; init; }
    public required double MicVolume { get; init; }
    public required bool HighlightCursor { get; init; }
    public required bool HighlightClicks { get; init; }
    public bool ShowKeystrokes { get; init; }
    public bool PipelinedEncoding { get; init; }
    public required string VideoSaveDirectory { get; init; }
    public required string RecordFormat { get; init; }
    public required VideoCodec VideoCodec { get; init; }
    public required VideoEncoderHint VideoEncoderHint { get; init; }
    public required VideoQuality VideoQuality { get; init; }
    public int CustomVideoCrf { get; init; }
    public int CustomVideoBitrateKbps { get; init; }
    public required int RecordFps { get; init; }
    public required double MaxRecordingSizeMb { get; init; }
    public required int PlaybackUiFps { get; init; }
    public required int PlaybackTimelineFps { get; init; }
    public bool HardwareDecodeEnabled { get; init; } = true;
    public required bool UseFixedRecordPath { get; init; }
    public required string TempDirectory { get; init; }
    public required string SnipHotkey { get; init; }
    public required string RecordHotkey { get; init; }
    public required string TranslateHotkey { get; init; }
    public required string TextCopyHotkey { get; init; }
    public required string ScrollingCaptureHotkey { get; init; }
    public required string AIResourcesDirectory { get; init; }
    public required bool EnableAI { get; init; }
    public required bool ShowAIScanBox { get; init; }
    public required bool EnableAIScan { get; init; }
    public required OCRLanguage SourceLanguage { get; init; }
    public required TranslationLanguage TargetLanguage { get; init; }
    public required TranslationEngine SelectedTranslationEngine { get; init; }
    public required string LlamaModelId { get; init; }
    public required string LlamaCustomModelPath { get; init; }
    public required int LlamaContextSize { get; init; }
    public required int LlamaGpuLayers { get; init; }

    public static MainWindowSettingsSnapshot FromAppSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new MainWindowSettingsSnapshot
        {
            Language = settings.Language,
            RunOnStartup = settings.RunOnStartup,
            AutoCheckUpdates = settings.AutoCheckUpdates,
            BorderThickness = settings.BorderThickness,
            BorderColor = ParseColor(settings.BorderColorHex, "#E60012"),
            ThemeColor = ParseColor(settings.ThemeColorHex, "#E60012"),
            WingScale = settings.WingScale,
            HideSnipPinDecoration = settings.HideSnipPinDecoration,
            HideSnipPinBorder = settings.HideSnipPinBorder,
            HideSnipSelectionDecoration = settings.HideSnipSelectionDecoration,
            CaptureWithoutStealingFocus = settings.CaptureWithoutStealingFocus,
            FreezeScreenOnScreenshot = settings.FreezeScreenOnScreenshot,
            CaptureDelay = settings.CaptureDelay,
            OcrTextLayout = settings.OcrTextLayout,
            SaveOcrTextToFile = settings.SaveOcrTextToFile,
            ScrollingCaptureDirection = settings.ScrollingCaptureDirection,
            SnipToolbarPosition = settings.SnipToolbarPosition,
            HideRecordPinDecoration = settings.HideRecordPinDecoration,
            HideRecordPinBorder = settings.HideRecordPinBorder,
            HideRecordSelectionDecoration = settings.HideRecordSelectionDecoration,
            DefaultHideSnipToolbar = settings.DefaultHideSnipToolbar,
            DefaultHideRecordToolbar = settings.DefaultHideRecordToolbar,
            AutoSave = settings.AutoSave,
            EnableHistory = settings.EnableHistory,
            RevealAfterSave = settings.RevealAfterSave,
            SaveDirectory = settings.SaveDirectory,
            FileNameTemplate = settings.FileNameTemplate,
            ImgurClientId = settings.ImgurClientId,
            ShowSnipCursor = settings.ShowSnipCursor,
            ShowRecordCursor = settings.ShowRecordCursor,
            RecordSystemAudio = settings.RecordSystemAudio,
            EnableWebcam = settings.EnableWebcam,
            WebcamDeviceName = settings.WebcamDeviceName,
            WebcamCorner = settings.WebcamCorner,
            WebcamSize = settings.WebcamSize,
            WebcamCircular = settings.WebcamCircular,
            RecordMicrophone = settings.RecordMicrophone,
            SelectedMicDeviceId = settings.SelectedMicDeviceId,
            MicVolume = settings.MicVolume,
            HighlightCursor = settings.HighlightCursor,
            HighlightClicks = settings.HighlightClicks,
            ShowKeystrokes = settings.ShowKeystrokes,
            PipelinedEncoding = settings.PipelinedEncoding,
            VideoSaveDirectory = settings.VideoSaveDirectory,
            RecordFormat = settings.RecordFormat,
            VideoCodec = settings.VideoCodec,
            VideoEncoderHint = settings.VideoEncoderHint,
            VideoQuality = settings.VideoQuality,
            CustomVideoCrf = settings.CustomVideoCrf,
            CustomVideoBitrateKbps = settings.CustomVideoBitrateKbps,
            RecordFps = settings.RecordFPS,
            MaxRecordingSizeMb = settings.MaxRecordingSizeMB,
            PlaybackUiFps = settings.PlaybackUiFps,
            PlaybackTimelineFps = settings.PlaybackTimelineFps,
            HardwareDecodeEnabled = settings.HardwareDecodeEnabled,
            UseFixedRecordPath = settings.UseFixedRecordPath,
            TempDirectory = settings.TempDirectory,
            SnipHotkey = settings.SnipHotkey,
            RecordHotkey = settings.RecordHotkey,
            TranslateHotkey = settings.TranslateHotkey,
            TextCopyHotkey = settings.TextCopyHotkey,
            ScrollingCaptureHotkey = settings.ScrollingCaptureHotkey,
            AIResourcesDirectory = settings.AIResourcesDirectory,
            EnableAI = settings.EnableAI,
            ShowAIScanBox = settings.ShowAIScanBox,
            EnableAIScan = settings.EnableAIScan,
            SourceLanguage = settings.SourceLanguage,
            TargetLanguage = settings.TargetLanguage,
            SelectedTranslationEngine = settings.SelectedTranslationEngine,
            LlamaModelId = settings.LlamaModelId,
            LlamaCustomModelPath = settings.LlamaCustomModelPath,
            LlamaContextSize = settings.LlamaContextSize,
            LlamaGpuLayers = settings.LlamaGpuLayers
        };
    }

    public void ApplyTo(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Language = Language;
        settings.RunOnStartup = RunOnStartup;
        settings.AutoCheckUpdates = AutoCheckUpdates;
        settings.BorderThickness = BorderThickness;
        settings.BorderColorHex = BorderColor.ToString();
        settings.ThemeColorHex = ThemeColor.ToString();
        settings.WingScale = WingScale;
        settings.HideSnipPinDecoration = HideSnipPinDecoration;
        settings.HideSnipPinBorder = HideSnipPinBorder;
        settings.HideSnipSelectionDecoration = HideSnipSelectionDecoration;
        settings.CaptureWithoutStealingFocus = CaptureWithoutStealingFocus;
        settings.FreezeScreenOnScreenshot = FreezeScreenOnScreenshot;
        settings.CaptureDelay = CaptureDelay;
        settings.OcrTextLayout = OcrTextLayout;
        settings.SaveOcrTextToFile = SaveOcrTextToFile;
        settings.ScrollingCaptureDirection = ScrollingCaptureDirection;
        settings.SnipToolbarPosition = SnipToolbarPosition;
        settings.HideRecordPinDecoration = HideRecordPinDecoration;
        settings.HideRecordPinBorder = HideRecordPinBorder;
        settings.HideRecordSelectionDecoration = HideRecordSelectionDecoration;
        settings.DefaultHideSnipToolbar = DefaultHideSnipToolbar;
        settings.DefaultHideRecordToolbar = DefaultHideRecordToolbar;
        settings.AutoSave = AutoSave;
        settings.EnableHistory = EnableHistory;
        settings.RevealAfterSave = RevealAfterSave;
        settings.SaveDirectory = SaveDirectory;
        settings.FileNameTemplate = FileNameTemplate;
        settings.ImgurClientId = ImgurClientId;
        settings.ShowSnipCursor = ShowSnipCursor;
        settings.ShowRecordCursor = ShowRecordCursor;
        settings.RecordSystemAudio = RecordSystemAudio;
        settings.EnableWebcam = EnableWebcam;
        settings.WebcamDeviceName = WebcamDeviceName;
        settings.WebcamCorner = WebcamCorner;
        settings.WebcamSize = WebcamSize;
        settings.WebcamCircular = WebcamCircular;
        settings.RecordMicrophone = RecordMicrophone;
        settings.SelectedMicDeviceId = SelectedMicDeviceId;
        settings.MicVolume = MicVolume;
        settings.HighlightCursor = HighlightCursor;
        settings.HighlightClicks = HighlightClicks;
        settings.ShowKeystrokes = ShowKeystrokes;
        settings.PipelinedEncoding = PipelinedEncoding;
        settings.VideoSaveDirectory = VideoSaveDirectory;
        settings.RecordFormat = RecordFormat;
        settings.VideoCodec = VideoCodec;
        settings.VideoEncoderHint = VideoEncoderHint;
        settings.VideoQuality = VideoQuality;
        settings.CustomVideoCrf = CustomVideoCrf;
        settings.CustomVideoBitrateKbps = CustomVideoBitrateKbps;
        settings.RecordFPS = RecordFps;
        settings.MaxRecordingSizeMB = MaxRecordingSizeMb;
        settings.PlaybackUiFps = PlaybackUiFps;
        settings.PlaybackTimelineFps = PlaybackTimelineFps;
        settings.HardwareDecodeEnabled = HardwareDecodeEnabled;
        settings.UseFixedRecordPath = UseFixedRecordPath;
        settings.TempDirectory = TempDirectory;
        settings.SnipHotkey = SnipHotkey;
        settings.RecordHotkey = RecordHotkey;
        settings.TranslateHotkey = TranslateHotkey;
        settings.TextCopyHotkey = TextCopyHotkey;
        settings.ScrollingCaptureHotkey = ScrollingCaptureHotkey;
        settings.AIResourcesDirectory = AIResourcesDirectory;
        settings.EnableAI = EnableAI;
        settings.ShowAIScanBox = ShowAIScanBox;
        settings.EnableAIScan = EnableAIScan;
        settings.SourceLanguage = SourceLanguage;
        settings.TargetLanguage = TargetLanguage;
        settings.SelectedTranslationEngine = SelectedTranslationEngine;
        settings.LlamaModelId = LlamaModelId;
        settings.LlamaCustomModelPath = LlamaCustomModelPath;
        settings.LlamaContextSize = LlamaContextSize;
        settings.LlamaGpuLayers = LlamaGpuLayers;
    }

    private static Color ParseColor(string colorHex, string fallbackHex)
    {
        if (Color.TryParse(colorHex, out var color))
        {
            return color;
        }

        return Color.Parse(fallbackHex);
    }
}
