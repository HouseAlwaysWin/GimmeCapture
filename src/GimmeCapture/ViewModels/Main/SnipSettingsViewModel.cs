using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Snip-mode settings extracted from <see cref="MainWindowViewModel"/> (Tier-4 split). Plain data holder,
/// same shape as <see cref="RecordingSettingsViewModel"/>: no side effects, no persistence of its own.
/// <see cref="MainWindowViewModel"/> exposes same-named forwarder properties that delegate here, so XAML,
/// persistence, commands, and cross-VM readers are unchanged; MainWindowViewModel re-raises these changes
/// (for its bindings) and its global PropertyChanged handler queues the save.
/// </summary>
public class SnipSettingsViewModel : ViewModelBase
{
    // Output
    private bool _autoSave;
    public bool AutoSave
    {
        get => _autoSave;
        set => this.RaiseAndSetIfChanged(ref _autoSave, value);
    }

    private bool _enableHistory = true;
    public bool EnableHistory
    {
        get => _enableHistory;
        set => this.RaiseAndSetIfChanged(ref _enableHistory, value);
    }

    private bool _revealAfterSave = true;
    public bool RevealAfterSave
    {
        get => _revealAfterSave;
        set => this.RaiseAndSetIfChanged(ref _revealAfterSave, value);
    }

    private string _saveDirectory = string.Empty;
    public string SaveDirectory
    {
        get => _saveDirectory;
        set => this.RaiseAndSetIfChanged(ref _saveDirectory, value);
    }

    private string _fileNameTemplate = string.Empty;
    public string FileNameTemplate
    {
        get => _fileNameTemplate;
        set => this.RaiseAndSetIfChanged(ref _fileNameTemplate, value);
    }

    // Pin & selection windows
    private bool _hideSnipPinDecoration;
    public bool HideSnipPinDecoration
    {
        get => _hideSnipPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideSnipPinDecoration, value);
    }

    private bool _hideSnipPinBorder;
    public bool HideSnipPinBorder
    {
        get => _hideSnipPinBorder;
        set => this.RaiseAndSetIfChanged(ref _hideSnipPinBorder, value);
    }

    private bool _defaultHideSnipToolbar;
    public bool DefaultHideSnipToolbar
    {
        get => _defaultHideSnipToolbar;
        set => this.RaiseAndSetIfChanged(ref _defaultHideSnipToolbar, value);
    }

    private bool _hideSnipSelectionDecoration;
    public bool HideSnipSelectionDecoration
    {
        get => _hideSnipSelectionDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideSnipSelectionDecoration, value);
    }

    private bool _autoPinScreenshotSelection;
    public bool AutoPinScreenshotSelection
    {
        get => _autoPinScreenshotSelection;
        set => this.RaiseAndSetIfChanged(ref _autoPinScreenshotSelection, value);
    }

    private bool _showSnipCursor;
    public bool ShowSnipCursor
    {
        get => _showSnipCursor;
        set => this.RaiseAndSetIfChanged(ref _showSnipCursor, value);
    }

    // Capture & OCR
    private CaptureDelay _captureDelay;
    public CaptureDelay CaptureDelay
    {
        get => _captureDelay;
        set => this.RaiseAndSetIfChanged(ref _captureDelay, value);
    }

    private OcrTextLayout _ocrTextLayout;
    public OcrTextLayout OcrTextLayout
    {
        get => _ocrTextLayout;
        set => this.RaiseAndSetIfChanged(ref _ocrTextLayout, value);
    }
}
