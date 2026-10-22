using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Media;
using GimmeCapture.Models;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// The settings the capture overlay reads, live, and the few its own toolbar changes. Each raises PropertyChanged, so an
/// open overlay follows a change. <see cref="MainWindowViewModel"/> implements it. The overlay's own hotkeys come from
/// the settings model instead (<see cref="SnipHotkeys"/> and the others).
/// </summary>
public interface ISnipOverlaySettings : INotifyPropertyChanged
{
    // The main window's global hotkeys: shown in tooltips, and told apart when one is pressed over the overlay.
    string SnipHotkey { get; }
    string RecordHotkey { get; }
    string TranslateHotkey { get; }
    string TextCopyHotkey { get; }
    string ScrollingCaptureHotkey { get; }

    // Look.
    Color ThemeColor { get; }
    Color BorderColor { get; }
    double BorderThickness { get; }
    SnipToolbarPosition SnipToolbarPosition { get; }
    double WingScale { get; set; }

    // Screenshots: saving, history and OCR text.
    string SaveDirectory { get; }
    string TempDirectory { get; }
    string FileNameTemplate { get; }
    bool AutoSave { get; }
    bool RevealAfterSave { get; }
    bool EnableHistory { get; }
    bool ShowSnipCursor { get; }
    bool HideSnipSelectionDecoration { get; }
    bool HideSnipPinDecoration { get; }
    bool HideSnipPinBorder { get; }
    bool DefaultHideSnipToolbar { get; }
    bool CaptureWithoutStealingFocus { get; }
    OcrTextLayout OcrTextLayout { get; }
    bool SaveOcrTextToFile { get; }
    ScrollingCaptureDirection ScrollingCaptureDirection { get; set; }

    // Recording.
    RecordingSettingsViewModel RecordingSettings { get; }
    bool ShowRecordCursor { get; }
    bool RecordSystemAudio { get; }
    bool HideRecordSelectionDecoration { get; }
    bool HideRecordPinDecoration { get; }
    bool HideRecordPinBorder { get; }
    bool DefaultHideRecordToolbar { get; }

    // AI and translation.
    bool EnableAI { get; }
    bool EnableAIScan { get; set; }
    bool ShowAIScanBox { get; set; }
    OCRLanguage SourceLanguage { get; set; }
    TranslationLanguage TargetLanguage { get; set; }
    List<OCRLanguage> AvailableOCRLanguages { get; }
    List<TranslationLanguage> AvailableTranslationLanguages { get; }

    /// <summary>Watched for changes only; the overlay reads the value along with its other hotkeys.</summary>
    string Translate_SelectionHoldModifier { get; }
}
