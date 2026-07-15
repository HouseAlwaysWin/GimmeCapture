using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;
using System.Net.Http;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.IO;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    // Language Selection
    public class LanguageOption
    {
        public string Name { get; set; } = string.Empty;
        public Language Value { get; set; }
    }

    public sealed record CaptureDelayOption(CaptureDelay Value, string DisplayName);
    public sealed record OcrTextLayoutOption(OcrTextLayout Value, string DisplayName);
    public sealed record ScrollDirectionOption(ScrollingCaptureDirection Value, string DisplayName);
    public sealed record SnipToolbarPositionOption(SnipToolbarPosition Value, string DisplayName);

    public IReadOnlyList<CaptureDelayOption> AvailableCaptureDelays =>
    [
        new(CaptureDelay.Off, LocalizationService.Instance["CaptureDelayOff"]),
        new(CaptureDelay.OneSecond, string.Format(LocalizationService.Instance["CaptureDelaySeconds"], 1)),
        new(CaptureDelay.ThreeSeconds, string.Format(LocalizationService.Instance["CaptureDelaySeconds"], 3)),
        new(CaptureDelay.FiveSeconds, string.Format(LocalizationService.Instance["CaptureDelaySeconds"], 5)),
        new(CaptureDelay.TenSeconds, string.Format(LocalizationService.Instance["CaptureDelaySeconds"], 10))
    ];

    public IReadOnlyList<OcrTextLayoutOption> AvailableOcrTextLayouts =>
    [
        new(OcrTextLayout.PreserveLines, LocalizationService.Instance["OcrPreserveLines"]),
        new(OcrTextLayout.SingleLine, LocalizationService.Instance["OcrSingleLine"])
    ];

    public IReadOnlyList<ScrollDirectionOption> AvailableScrollDirections =>
    [
        new(ScrollingCaptureDirection.Auto, LocalizationService.Instance["ScrollDirectionAuto"]),
        new(ScrollingCaptureDirection.Vertical, LocalizationService.Instance["ScrollDirectionVertical"]),
        new(ScrollingCaptureDirection.Horizontal, LocalizationService.Instance["ScrollDirectionHorizontal"])
    ];

    public IReadOnlyList<SnipToolbarPositionOption> AvailableSnipToolbarPositions =>
    [
        new(SnipToolbarPosition.TopLeft, LocalizationService.Instance["SnipToolbarPositionTopLeft"]),
        new(SnipToolbarPosition.TopCenter, LocalizationService.Instance["SnipToolbarPositionTopCenter"]),
        new(SnipToolbarPosition.TopRight, LocalizationService.Instance["SnipToolbarPositionTopRight"])
    ];

    public List<TranslationLanguage> AvailableTranslationLanguages =>
        Enum.GetValues<TranslationLanguage>().AsValueEnumerable().ToList();

    public List<OCRLanguage> AvailableOCRLanguages =>
        Enum.GetValues<OCRLanguage>().AsValueEnumerable().ToList();

    private OCRLanguage _sourceLanguage;
    public OCRLanguage SourceLanguage
    {
        get => _sourceLanguage;
        set
        {
            if (_sourceLanguage != value)
            {
                this.RaiseAndSetIfChanged(ref _sourceLanguage, value);
                
                if (!_isDataLoading)
                {
                   _settingsService.Settings.SourceLanguage = value; // Immediate sync
                   MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    private TranslationLanguage _targetLanguage;
    public TranslationLanguage TargetLanguage
    {
        get => _targetLanguage;
        set
        {
            if (_targetLanguage != value)
            {
                this.RaiseAndSetIfChanged(ref _targetLanguage, value);
                
                if (!_isDataLoading)
                {
                   _settingsService.Settings.TargetLanguage = value; // Immediate sync
                   MarkModifiedAndQueueSettingsSave(); // Auto-save for convenience
                }
            }
        }
    }

    public List<TranslationEngine> AvailableTranslationEngines { get; } = new() { TranslationEngine.LlamaSharp };
    
    private TranslationEngine _selectedTranslationEngine;
    public TranslationEngine SelectedTranslationEngine
    {
        get => _selectedTranslationEngine;
        set
        {
            if (_selectedTranslationEngine != value)
            {
                this.RaiseAndSetIfChanged(ref _selectedTranslationEngine, value);
                this.RaisePropertyChanged(nameof(IsLlamaVisible));
                
                // Notify language lists changed
                this.RaisePropertyChanged(nameof(AvailableOCRLanguages));
                this.RaisePropertyChanged(nameof(AvailableTranslationLanguages));

                if (!_isDataLoading)
                {
                    _settingsService.Settings.SelectedTranslationEngine = value; // Immediate sync
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    public bool IsLlamaVisible => SelectedTranslationEngine == TranslationEngine.LlamaSharp;

    public LanguageOption[] AvailableLanguages { get; } = new[]
    {
        new LanguageOption { Name = "English (US)", Value = Language.English },
        new LanguageOption { Name = "繁體中文 (台灣)", Value = Language.Chinese },
        new LanguageOption { Name = "日本語 (日本)", Value = Language.Japanese }
    };

    public string AIResourcesDirectory
    {
        get => string.IsNullOrEmpty(_settingsService.Settings.AIResourcesDirectory) 
               ? AppStoragePaths.GetSharedAIResourcesDirectory(RuntimePathProvider.GetExecutableDirectory()) 
               : _settingsService.Settings.AIResourcesDirectory;
        set
        {
            _settingsService.Settings.AIResourcesDirectory = value;
            this.RaisePropertyChanged();
            IsModified = true;
        }
    }

    public LanguageOption SelectedLanguageOption
    {
        get => AvailableLanguages.AsValueEnumerable().FirstOrDefault(x => x.Value == LocalizationService.Instance.CurrentLanguage) ?? AvailableLanguages[0];
        set
        {
            if (value != null && LocalizationService.Instance.CurrentLanguage != value.Value)
            {
                LocalizationService.Instance.CurrentLanguage = value.Value;
                this.RaisePropertyChanged();
                
                if (!_isDataLoading)
                {
                    _settingsService.Settings.Language = value.Value;
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set
        {
            if (_runOnStartup != value)
            {
                this.RaiseAndSetIfChanged(ref _runOnStartup, value);
                _settingsSideEffectCoordinator.ApplyRunOnStartup(value);
                if (!_isDataLoading)
                {
                    _settingsService.Settings.RunOnStartup = value;
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    private bool _autoCheckUpdates;
    public bool AutoCheckUpdates
    {
        get => _autoCheckUpdates;
        set
        {
            if (_autoCheckUpdates != value)
            {
                this.RaiseAndSetIfChanged(ref _autoCheckUpdates, value);
                if (!_isDataLoading)
                {
                    _settingsService.Settings.AutoCheckUpdates = value;
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    // Snip Settings
    private double _borderThickness;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private double _wingScale = 1.0;
    public double WingScale
    {
        get => _wingScale;
        set 
        {
            this.RaiseAndSetIfChanged(ref _wingScale, value);
            this.RaisePropertyChanged(nameof(PreviewWingWidth));
            this.RaisePropertyChanged(nameof(PreviewWingHeight));
            this.RaisePropertyChanged(nameof(PreviewLeftWingMargin));
            this.RaisePropertyChanged(nameof(PreviewRightWingMargin));
        }
    }

    public double PreviewIconSize => 10;
    public double PreviewWingWidth => 100 * WingScale * 0.5;
    public double PreviewWingHeight => 60 * WingScale * 0.5;
    public Thickness PreviewLeftWingMargin => new Thickness(-PreviewWingWidth, 0, 0, 0);
    public Thickness PreviewRightWingMargin => new Thickness(0, 0, -PreviewWingWidth, 0);
    
    private Color _borderColor;
    public Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    private Color _themeColor;
    public Color ThemeColor
    {
        get => _themeColor;
        set 
        {
            var old = _themeColor;
            this.RaiseAndSetIfChanged(ref _themeColor, value);
            if (old != value)
            {
                _settingsSideEffectCoordinator.ApplyThemeColors(value, ThemeDeepColor);
                this.RaisePropertyChanged(nameof(ThemeDeepColor));
            }
        }
    }

    public Color ThemeDeepColor => ThemeColorPalette.GetDeepColor(ThemeColor);

    // Output Settings
    public bool AutoSave
    {
        get => Snip.AutoSave;
        set => Snip.AutoSave = value;
    }

    public bool EnableHistory
    {
        get => Snip.EnableHistory;
        set => Snip.EnableHistory = value;
    }

    public bool RevealAfterSave
    {
        get => Snip.RevealAfterSave;
        set => Snip.RevealAfterSave = value;
    }

    public string SaveDirectory
    {
        get => Snip.SaveDirectory;
        set => Snip.SaveDirectory = value;
    }
    
    // Control Settings
    private string _snipHotkey = "Shift+F1";
    public string SnipHotkey
    {
        get => _snipHotkey;
        set
        {
            var changed = _snipHotkey != value;
            if (changed)
            {
                this.RaiseAndSetIfChanged(ref _snipHotkey, value);
            }
            _settingsSideEffectCoordinator.RegisterGlobalHotkey(HotkeyIds.Snip, value);
            if (changed)
            {
                this.RaisePropertyChanged(nameof(SnipTooltip));
                if (!_isDataLoading)
                {
                    _settingsService.Settings.SnipHotkey = value;
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }


    private string _translateHotkey = "Shift+F3";
    public string TranslateHotkey
    {
        get => _translateHotkey;
        set
        {
            var changed = _translateHotkey != value;
            if (changed)
            {
                this.RaiseAndSetIfChanged(ref _translateHotkey, value);
            }
            _settingsSideEffectCoordinator.RegisterGlobalHotkey(HotkeyIds.Translate, value);
            if (changed)
            {
                this.RaisePropertyChanged(nameof(TranslateTooltip));
                if (!_isDataLoading)
                {
                    _settingsService.Settings.TranslateHotkey = value;
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    private string _recordHotkey = "Shift+F2";
    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            var changed = _recordHotkey != value;
            if (changed)
            {
                this.RaiseAndSetIfChanged(ref _recordHotkey, value);
            }
            _settingsSideEffectCoordinator.RegisterGlobalHotkey(HotkeyIds.Record, value);
            if (changed)
            {
                this.RaisePropertyChanged(nameof(RecordTooltip));
                if (!_isDataLoading)
                {
                    _settingsService.Settings.RecordHotkey = value;
                    MarkModifiedAndQueueSettingsSave();
                }
            }
        }
    }

    public string SnipTooltip => $"{LocalizationService.Instance["StartCapture"]} ({SnipHotkey})";
    public string RecordTooltip => $"{LocalizationService.Instance["CaptureModeRecord"]} ({RecordHotkey})";
    public string TranslateTooltip => $"{LocalizationService.Instance["TranslateHotkey"]} ({TranslateHotkey})";

    // Action Hotkeys

    public RecordingSettingsViewModel RecordingSettings { get; } = new();

    /// <summary>Snip-mode settings (Tier-4 split); MainWindowViewModel exposes same-named forwarders.</summary>
    public SnipSettingsViewModel Snip { get; } = new();

    public bool HideSnipPinDecoration
    {
        get => Snip.HideSnipPinDecoration;
        set => Snip.HideSnipPinDecoration = value;
    }

    public bool HideSnipPinBorder
    {
        get => Snip.HideSnipPinBorder;
        set => Snip.HideSnipPinBorder = value;
    }
    
    public bool DefaultHideSnipToolbar
    {
        get => Snip.DefaultHideSnipToolbar;
        set => Snip.DefaultHideSnipToolbar = value;
    }

    private bool _defaultHideRecordToolbar = false;
    public bool DefaultHideRecordToolbar
    {
        get => _defaultHideRecordToolbar;
        set => this.RaiseAndSetIfChanged(ref _defaultHideRecordToolbar, value);
    }

    private bool _hideRecordPinDecoration = false;
    public bool HideRecordPinDecoration
    {
        get => _hideRecordPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideRecordPinDecoration, value);
    }

    private bool _hideRecordPinBorder = false;
    public bool HideRecordPinBorder
    {
        get => _hideRecordPinBorder;
        set => this.RaiseAndSetIfChanged(ref _hideRecordPinBorder, value);
    }

    public bool HideSnipSelectionDecoration
    {
        get => Snip.HideSnipSelectionDecoration;
        set => Snip.HideSnipSelectionDecoration = value;
    }

    public bool AutoPinScreenshotSelection
    {
        get => Snip.AutoPinScreenshotSelection;
        set => Snip.AutoPinScreenshotSelection = value;
    }

    public CaptureDelay CaptureDelay
    {
        get => Snip.CaptureDelay;
        set => Snip.CaptureDelay = value;
    }

    public OcrTextLayout OcrTextLayout
    {
        get => Snip.OcrTextLayout;
        set => Snip.OcrTextLayout = value;
    }

    private ScrollingCaptureDirection _scrollingCaptureDirection;
    public ScrollingCaptureDirection ScrollingCaptureDirection
    {
        get => _scrollingCaptureDirection;
        set
        {
            var changed = _scrollingCaptureDirection != value;
            this.RaiseAndSetIfChanged(ref _scrollingCaptureDirection, value);
            if (changed && !_isDataLoading)
            {
                _settingsService.Settings.ScrollingCaptureDirection = value;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    // Backing field defaults to TopCenter to match AppSettings (the enum's numeric default is TopLeft).
    private SnipToolbarPosition _snipToolbarPosition = SnipToolbarPosition.TopCenter;
    public SnipToolbarPosition SnipToolbarPosition
    {
        get => _snipToolbarPosition;
        set
        {
            var changed = _snipToolbarPosition != value;
            this.RaiseAndSetIfChanged(ref _snipToolbarPosition, value);
            if (changed && !_isDataLoading)
            {
                _settingsService.Settings.SnipToolbarPosition = value;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    private bool _hideRecordSelectionDecoration = false;
    public bool HideRecordSelectionDecoration
    {
        get => _hideRecordSelectionDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideRecordSelectionDecoration, value);
    }

    private string _tempDirectory = string.Empty;
    public string TempDirectory
    {
        get => _tempDirectory;
        set => this.RaiseAndSetIfChanged(ref _tempDirectory, value);
    }

    public bool ShowSnipCursor
    {
        get => Snip.ShowSnipCursor;
        set => Snip.ShowSnipCursor = value;
    }

    public bool ShowAIScanBox
    {
        get => _settingsService.Settings.ShowAIScanBox;
        set
        {
            if (_settingsService.Settings.ShowAIScanBox != value)
            {
                _settingsService.Settings.ShowAIScanBox = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(EnableOcrSelectionDetection));
            }
        }
    }

    public bool EnableAIScan
    {
        get => _settingsService.Settings.EnableAIScan;
        set
        {
            if (_settingsService.Settings.EnableAIScan != value)
            {
                _settingsService.Settings.EnableAIScan = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(EnableOcrSelectionDetection));
                if (!_isDataLoading)
                {
                    QueueSettingsSave();
                }
            }
        }
    }

    public bool EnableOcrSelectionDetection
    {
        get => _settingsService.Settings.EnableAIScan && _settingsService.Settings.ShowAIScanBox;
        set
        {
            bool changed = _settingsService.Settings.EnableAIScan != value
                || _settingsService.Settings.ShowAIScanBox != value;
            if (!changed)
            {
                return;
            }

            _settingsService.Settings.EnableAIScan = value;
            _settingsService.Settings.ShowAIScanBox = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(EnableAIScan));
            this.RaisePropertyChanged(nameof(ShowAIScanBox));

            if (!_isDataLoading)
            {
                // Turning ON OCR selection detection implies wanting local AI on. Without this, the master
                // EnableAI can be OFF while this sub-toggle is ON, and the OCR auto-scan silently aborts at the
                // EnableAI gate in RunOCRScanAsync — nothing ever appears and there is no visible reason.
                // Force the master on so this toggle can never be a dead switch. Only on an explicit user toggle
                // (guarded by !_isDataLoading) so loading a saved config never overrides a persisted EnableAI.
                if (value && !EnableAI)
                {
                    EnableAI = true;
                }
                QueueSettingsSave();
            }
        }
    }

    private bool _enableAI = true;
    public bool EnableAI
    {
        get => _enableAI;
        set 
        {
            this.RaiseAndSetIfChanged(ref _enableAI, value);
            if (!_isDataLoading)
            {
                _settingsService.Settings.EnableAI = value;
                QueueSettingsSave();

                // 使用者關閉 AI 時，主動釋放 SAM2 模型記憶體
                if (!value)
                {
                    SAM2RuntimeService.UnloadModels();
                }
            }
        }
    }

    private string _llamaModelId = "translategemma-4b-it";
    public string LlamaModelId
    {
        get => _llamaModelId;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            this.RaiseAndSetIfChanged(ref _llamaModelId, value);
            SyncSelectedLlamaModelSelection();
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));
            if (!_isDataLoading)
            {
                _settingsService.Settings.LlamaModelId = value;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    private string _llamaCustomModelPath = string.Empty;
    public string LlamaCustomModelPath
    {
        get => _llamaCustomModelPath;
        set
        {
            this.RaiseAndSetIfChanged(ref _llamaCustomModelPath, value);
            if (!_isDataLoading)
            {
                _settingsService.Settings.LlamaCustomModelPath = value;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    private int _llamaContextSize = 2048;
    public int LlamaContextSize
    {
        get => _llamaContextSize;
        set
        {
            this.RaiseAndSetIfChanged(ref _llamaContextSize, IntParameterValidator.ClampLlamaContextSize(value));
            if (!_isDataLoading)
            {
                _settingsService.Settings.LlamaContextSize = _llamaContextSize;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    private int _llamaGpuLayers;
    public int LlamaGpuLayers
    {
        get => _llamaGpuLayers;
        set
        {
            this.RaiseAndSetIfChanged(ref _llamaGpuLayers, IntParameterValidator.ClampGpuLayers(value));
            if (!_isDataLoading)
            {
                _settingsService.Settings.LlamaGpuLayers = _llamaGpuLayers;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    public sealed class LlamaModelOption
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }

        public override string ToString() => DisplayName;
    }

    private ObservableCollection<LlamaModelOption> _availableLlamaModels = new(
    [
        ToLlamaModelOption("translategemma-4b-it"),
        ToLlamaModelOption("gemma-3-4b-it-q4"),
        ToLlamaModelOption("translategemma-12b-it")
    ]);
    public ObservableCollection<LlamaModelOption> AvailableLlamaModels
    {
        get => _availableLlamaModels;
        private set
        {
            this.RaiseAndSetIfChanged(ref _availableLlamaModels, value);
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));
        }
    }

    private LlamaModelOption? _selectedLlamaModelOption;
    public LlamaModelOption? SelectedLlamaModelOption
    {
        get => _selectedLlamaModelOption;
        set
        {
            if (ReferenceEquals(_selectedLlamaModelOption, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedLlamaModelOption, value);
            SyncSelectedLlamaModelIndexCore(value);
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));

            if (value != null && !string.Equals(LlamaModelId, value.Id, StringComparison.Ordinal))
            {
                LlamaModelId = value.Id;
            }
        }
    }

    private string _textCopyHotkey = "Shift+F4";
    public string TextCopyHotkey
    {
        get => _textCopyHotkey;
        set
        {
            var changed = _textCopyHotkey != value;
            if (changed)
            {
                this.RaiseAndSetIfChanged(ref _textCopyHotkey, value);
            }

            _settingsSideEffectCoordinator.RegisterGlobalHotkey(HotkeyIds.TextCopy, value);
            if (changed && !_isDataLoading)
            {
                _settingsService.Settings.TextCopyHotkey = value;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    private string _scrollingCaptureHotkey = "Shift+F5";
    public string ScrollingCaptureHotkey
    {
        get => _scrollingCaptureHotkey;
        set
        {
            var changed = _scrollingCaptureHotkey != value;
            if (changed)
            {
                this.RaiseAndSetIfChanged(ref _scrollingCaptureHotkey, value);
            }

            _settingsSideEffectCoordinator.RegisterGlobalHotkey(HotkeyIds.ScrollingCapture, value);
            if (changed && !_isDataLoading)
            {
                _settingsService.Settings.ScrollingCaptureHotkey = value;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    private bool _isLlamaModelPickerOpen;
    public bool IsLlamaModelPickerOpen
    {
        get => _isLlamaModelPickerOpen;
        set => this.RaiseAndSetIfChanged(ref _isLlamaModelPickerOpen, value);
    }

    public string SelectedLlamaModelDisplayName =>
        SelectedLlamaModelOption?.DisplayName
        ?? ToLlamaModelOption(LlamaModelId).DisplayName;

    private int _selectedLlamaModelIndex = -1;
    public int SelectedLlamaModelIndex
    {
        get => _selectedLlamaModelIndex;
        set
        {
            if (_selectedLlamaModelIndex == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedLlamaModelIndex, value);

            LlamaModelOption? nextOption = value >= 0 && value < AvailableLlamaModels.Count
                ? AvailableLlamaModels[value]
                : null;

            if (!ReferenceEquals(_selectedLlamaModelOption, nextOption))
            {
                _selectedLlamaModelOption = nextOption;
                this.RaisePropertyChanged(nameof(SelectedLlamaModelOption));
            }

            if (nextOption != null && !string.Equals(LlamaModelId, nextOption.Id, StringComparison.Ordinal))
            {
                LlamaModelId = nextOption.Id;
            }
        }
    }

    public ReactiveCommand<Unit, Unit> RefreshLlamaModelsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RefreshWebcamDevicesCommand { get; private set; } = null!;
    public bool HasDownloadedLlamaModels => AIResourceService.GetInstalledLlamaModelPresets().Count > 0 || AIResourceService.IsLlamaModelReady();
    public bool NoDownloadedLlamaModels => !HasDownloadedLlamaModels;

    public void RefreshLlamaModelCatalog()
    {
        var presets = AIResourceService.GetDownloadableLlamaModelPresets();
        var nextModels = new List<LlamaModelOption>();
        foreach (var preset in presets)
        {
            nextModels.Add(ToLlamaModelOption(preset.Id));
        }

        if (nextModels.Count == 0)
        {
            nextModels.Add(ToLlamaModelOption("translategemma-4b-it"));
            nextModels.Add(ToLlamaModelOption("gemma-3-4b-it-q4"));
            nextModels.Add(ToLlamaModelOption("translategemma-12b-it"));
        }

        string nextSelectedModelId = LlamaModelId;
        if (nextModels.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(nextSelectedModelId)
                || !nextModels.AsValueEnumerable().Any(static option => !string.IsNullOrWhiteSpace(option.Id))
                || !nextModels.AsValueEnumerable().Any(option => string.Equals(option.Id, nextSelectedModelId, StringComparison.Ordinal)))
            {
                nextSelectedModelId = nextModels[0].Id;
            }
        }
        else if (!AIResourceService.IsLlamaModelReady())
        {
            StatusText = LocalizationService.Instance["StatusLlamaModelNotReady"];
        }

        ReplaceLlamaModelOptions(nextModels);

        if (nextModels.Count > 0 && !string.Equals(LlamaModelId, nextSelectedModelId, StringComparison.Ordinal))
        {
            LlamaModelId = nextSelectedModelId;
        }
        else
        {
            SyncSelectedLlamaModelSelection();
        }

        this.RaisePropertyChanged(nameof(HasDownloadedLlamaModels));
        this.RaisePropertyChanged(nameof(NoDownloadedLlamaModels));
    }

    private void ReplaceLlamaModelOptions(IEnumerable<LlamaModelOption> nextModels)
    {
        var collection = new ObservableCollection<LlamaModelOption>(nextModels);
        AvailableLlamaModels = collection;
    }

    private void SyncSelectedLlamaModelSelection()
    {
        var nextOption = AvailableLlamaModels.AsValueEnumerable()
            .FirstOrDefault(option => string.Equals(option.Id, LlamaModelId, StringComparison.Ordinal));

        if (!ReferenceEquals(_selectedLlamaModelOption, nextOption))
        {
            _selectedLlamaModelOption = nextOption;
            this.RaisePropertyChanged(nameof(SelectedLlamaModelOption));
            this.RaisePropertyChanged(nameof(SelectedLlamaModelDisplayName));
        }

        SyncSelectedLlamaModelIndexCore(nextOption);
    }

    private void SyncSelectedLlamaModelIndexCore(LlamaModelOption? option)
    {
        int nextIndex = ModelOptionSelector.FindIndexById(option, AvailableLlamaModels, m => m.Id);

        if (_selectedLlamaModelIndex != nextIndex)
        {
            _selectedLlamaModelIndex = nextIndex;
            this.RaisePropertyChanged(nameof(SelectedLlamaModelIndex));
        }
    }

    private static LlamaModelOption ToLlamaModelOption(string modelId) =>
        new() { Id = modelId, DisplayName = LlamaModelDisplayNames.Get(modelId) };

    private bool _showRecordCursor = true;
    public bool ShowRecordCursor
    {
        get => _showRecordCursor;
        set => this.RaiseAndSetIfChanged(ref _showRecordCursor, value);
    }

    private bool _recordSystemAudio = true;
    public bool RecordSystemAudio
    {
        get => _recordSystemAudio;
        set => this.RaiseAndSetIfChanged(ref _recordSystemAudio, value);
    }

    private bool _enableWebcam = false;
    public bool EnableWebcam
    {
        get => _enableWebcam;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableWebcam, value);
            // Auto-list cameras the moment the feature is switched on, so the dropdown is ready.
            if (value && WebcamDevices.Count == 0)
            {
                RefreshWebcamDevices();
            }
        }
    }

    private string _webcamDeviceName = string.Empty;
    public string WebcamDeviceName
    {
        get => _webcamDeviceName;
        set => this.RaiseAndSetIfChanged(ref _webcamDeviceName, value);
    }

    // Auto-detected webcam names (DirectShow on Windows, V4L2 /dev/video* on Linux). The settings combo is
    // editable, so a hand-typed name (or a /dev/videoN path on Linux) still works if enumeration comes back
    // empty (e.g. a virtual camera not exposed to dshow).
    public ObservableCollection<string> WebcamDevices { get; } = new();

    /// <summary>Re-enumerates connected webcams and, if nothing is selected yet, picks the first one.</summary>
    public void RefreshWebcamDevices()
    {
        try
        {
            var found = Services.Core.Media.VideoInputDevices.Enumerate();
            WebcamDevices.Clear();
            foreach (var device in found)
            {
                WebcamDevices.Add(device.Name);
            }

            if (string.IsNullOrWhiteSpace(WebcamDeviceName) && WebcamDevices.Count > 0)
            {
                WebcamDeviceName = WebcamDevices[0];
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("MainWindowViewModel.RefreshWebcamDevices", ex);
        }
    }

    private int _webcamCorner = 3;
    public int WebcamCorner
    {
        get => _webcamCorner;
        set => this.RaiseAndSetIfChanged(ref _webcamCorner, value);
    }

    // Corner choices for the webcam PiP combo (value = corner index used by the encoder).
    public IReadOnlyList<WebcamCornerOption> WebcamCornerOptions { get; } = new[]
    {
        new WebcamCornerOption(0, "WebcamCornerTopLeft"),
        new WebcamCornerOption(1, "WebcamCornerTopRight"),
        new WebcamCornerOption(2, "WebcamCornerBottomLeft"),
        new WebcamCornerOption(3, "WebcamCornerBottomRight"),
    };

    public sealed record WebcamCornerOption(int Value, string LocalizationKey)
    {
        public string Name => LocalizationService.Instance[LocalizationKey];
    }

    private int _webcamSize = 1;
    public int WebcamSize
    {
        get => _webcamSize;
        set => this.RaiseAndSetIfChanged(ref _webcamSize, value);
    }

    // PiP size choices (value = size index used by the encoder: 0=Small, 1=Medium, 2=Large).
    public IReadOnlyList<WebcamCornerOption> WebcamSizeOptions { get; } = new[]
    {
        new WebcamCornerOption(0, "WebcamSizeSmall"),
        new WebcamCornerOption(1, "WebcamSizeMedium"),
        new WebcamCornerOption(2, "WebcamSizeLarge"),
    };

    private bool _webcamCircular = false;
    public bool WebcamCircular
    {
        get => _webcamCircular;
        set => this.RaiseAndSetIfChanged(ref _webcamCircular, value);
    }

    // Advanced encode overrides for recording (0 = auto): software CRF (1-51) / hardware bitrate (kbps).
    private int _customVideoCrf = 0;
    public int CustomVideoCrf
    {
        get => _customVideoCrf;
        set => this.RaiseAndSetIfChanged(ref _customVideoCrf, value);
    }

    private int _customVideoBitrateKbps = 0;
    public int CustomVideoBitrateKbps
    {
        get => _customVideoBitrateKbps;
        set => this.RaiseAndSetIfChanged(ref _customVideoBitrateKbps, value);
    }

    private bool _recordMicrophone = false;
    public bool RecordMicrophone
    {
        get => _recordMicrophone;
        set => this.RaiseAndSetIfChanged(ref _recordMicrophone, value);
    }

    private string _selectedMicDeviceId = string.Empty;
    public string SelectedMicDeviceId
    {
        get => _selectedMicDeviceId;
        set => this.RaiseAndSetIfChanged(ref _selectedMicDeviceId, value);
    }

    private double _micVolume = 1.0;
    public double MicVolume
    {
        get => _micVolume;
        set => this.RaiseAndSetIfChanged(ref _micVolume, Math.Clamp(value, 0.0, 2.0));
    }

    // Available microphone / capture devices (first entry = system default, empty id).
    public System.Collections.Generic.IReadOnlyList<GimmeCapture.Services.Core.Media.AudioInputDevice> MicInputDevices { get; } =
        GimmeCapture.Services.Core.Media.AudioInputDevices.Enumerate();

    private bool _highlightCursor = false;
    public bool HighlightCursor
    {
        get => _highlightCursor;
        set => this.RaiseAndSetIfChanged(ref _highlightCursor, value);
    }

    private bool _highlightClicks = false;
    public bool HighlightClicks
    {
        get => _highlightClicks;
        set => this.RaiseAndSetIfChanged(ref _highlightClicks, value);
    }

    private bool _showKeystrokes = false;
    public bool ShowKeystrokes
    {
        get => _showKeystrokes;
        set => this.RaiseAndSetIfChanged(ref _showKeystrokes, value);
    }

    private bool _pipelinedEncoding = false;
    public bool PipelinedEncoding
    {
        get => _pipelinedEncoding;
        set => this.RaiseAndSetIfChanged(ref _pipelinedEncoding, value);
    }

    private int _playbackUiFps = 30;
    public int PlaybackUiFps
    {
        get => _playbackUiFps;
        set => this.RaiseAndSetIfChanged(ref _playbackUiFps, IntParameterValidator.ClampPlaybackFps(value));
    }

    private int _playbackTimelineFps = 15;
    public int PlaybackTimelineFps
    {
        get => _playbackTimelineFps;
        set => this.RaiseAndSetIfChanged(ref _playbackTimelineFps, IntParameterValidator.ClampPlaybackFps(value));
    }

    private bool _hardwareDecodeEnabled = true;
    public bool HardwareDecodeEnabled
    {
        get => _hardwareDecodeEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _hardwareDecodeEnabled, value);
            // Drive the decoder's process-wide switch so the choice takes effect on the next playback open.
            Services.Core.Media.NativeFFmpeg.LibavVideoFramePlayer.HardwareDecodeEnabled = value;
        }
    }

    public bool IsGifAvailable => RecordingFormatCapabilities.IsGifAvailable();
    public string GifUnavailableReason => LocalizationService.Instance["GifUnavailableReason"];
    public string[] AvailableRecordFormats => IsGifAvailable
        ? ["mp4", "mkv", "gif", "webm", "mov"]
        : ["mp4", "mkv", "webm", "mov"];

    public async Task LoadSettingsAsync()
    {
        _isDataLoading = true;
        try
        {
            var snapshot = await _settingsPersistenceService.LoadAsync(_settingsService);
            ApplySettingsSnapshot(snapshot);

            // When run-on-startup is enabled, re-assert the OS registration on every launch, re-writing it with
            // the CURRENT executable path. SetStartup is otherwise only called when the user toggles the option,
            // so a drifted Run entry — a stale exe path after a reinstall/update, or a value that got wiped —
            // silently stops auto-start with no way to self-heal. Re-applying it here fixes it after one launch.
            // (The off case needs no work: toggling off already removes the entry.)
            if (RunOnStartup)
            {
                _settingsSideEffectCoordinator.ApplyRunOnStartup(true);
            }

            RefreshLlamaModelCatalog();
            RaiseSettingsBackedPropertyNotifications();
            IsModified = false;
        }
        catch (Exception ex)
        {
            AppLog.Warning("MainWindow.LoadSettings", ex);
        }
        finally
        {
            _isDataLoading = false;

            if (AutoCheckUpdates) _ = CheckForUpdates(true);
        }
    }

    public async Task<bool> SaveSettingsAsync()
    {
        if (_isDataLoading) return false;

        try
        {
            await _settingsPersistenceService.SaveAsync(_settingsService, CreateSettingsSnapshot());
            IsModified = false;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("MainWindow.SaveSettings", ex);
            return false;
        }
    }
}
