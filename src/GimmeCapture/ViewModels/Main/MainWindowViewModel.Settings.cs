using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using ReactiveUI;
using System.Net.Http;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;

namespace GimmeCapture.ViewModels.Main;

public partial class MainWindowViewModel
{
    // Language Selection
    public class LanguageOption
    {
        public string Name { get; set; } = string.Empty;
        public Language Value { get; set; }
    }

    public List<TranslationLanguage> AvailableTranslationLanguages => 
        _selectedTranslationEngine == TranslationEngine.MarianMT
            ? Enum.GetValues<TranslationLanguage>().Where(l => l != TranslationLanguage.Korean).ToList()
            : Enum.GetValues<TranslationLanguage>().ToList();

    public List<OCRLanguage> AvailableOCRLanguages => 
        _selectedTranslationEngine == TranslationEngine.MarianMT
            ? Enum.GetValues<OCRLanguage>().Where(l => l != OCRLanguage.Korean).ToList()
            : Enum.GetValues<OCRLanguage>().ToList();

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
                   IsModified = true;
                   _ = SaveSettingsAsync();
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
                   IsModified = true; // Mark as modified so Save can happen if auto-save or manual save
                   _ = SaveSettingsAsync(); // Auto-save for convenience
                }
            }
        }
    }

    public List<TranslationEngine> AvailableTranslationEngines { get; } = Enum.GetValues<TranslationEngine>().ToList();
    
    private TranslationEngine _selectedTranslationEngine;
    public TranslationEngine SelectedTranslationEngine
    {
        get => _selectedTranslationEngine;
        set
        {
            if (_selectedTranslationEngine != value)
            {
                this.RaiseAndSetIfChanged(ref _selectedTranslationEngine, value);
                this.RaisePropertyChanged(nameof(IsOllamaVisible));
                
                // Notify language lists changed
                this.RaisePropertyChanged(nameof(AvailableOCRLanguages));
                this.RaisePropertyChanged(nameof(AvailableTranslationLanguages));

                // Auto-reset illegal selections for MarianMT
                if (value == TranslationEngine.MarianMT)
                {
                    if (SourceLanguage == OCRLanguage.Korean)
                    {
                        SourceLanguage = OCRLanguage.Auto;
                    }
                    if (TargetLanguage == TranslationLanguage.Korean)
                    {
                        TargetLanguage = TranslationLanguage.TraditionalChinese;
                    }
                }

                if (!_isDataLoading)
                {
                    IsModified = true;
                    _ = SaveSettingsAsync();
                }
            }
        }
    }

    public bool IsOllamaVisible => SelectedTranslationEngine == TranslationEngine.Ollama;

    public LanguageOption[] AvailableLanguages { get; } = new[]
    {
        new LanguageOption { Name = "English (US)", Value = Language.English },
        new LanguageOption { Name = "繁體中文 (台灣)", Value = Language.Chinese },
        new LanguageOption { Name = "日本語 (日本)", Value = Language.Japanese }
    };

    public string AIResourcesDirectory
    {
        get => string.IsNullOrEmpty(_settingsService.Settings.AIResourcesDirectory) 
               ? System.IO.Path.Combine(_settingsService.BaseDataDirectory, "AI") 
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
        get => AvailableLanguages.FirstOrDefault(x => x.Value == LocalizationService.Instance.CurrentLanguage) ?? AvailableLanguages[0];
        set
        {
            if (value != null && LocalizationService.Instance.CurrentLanguage != value.Value)
            {
                LocalizationService.Instance.CurrentLanguage = value.Value;
                this.RaisePropertyChanged();
                
                if (!_isDataLoading)
                {
                    _settingsService.Settings.Language = value.Value;
                    IsModified = true;
                    _ = SaveSettingsAsync();
                }
            }
        }
    }

    private bool _runOnStartup;
    public bool RunOnStartup
    {
        get => _runOnStartup;
        set => this.RaiseAndSetIfChanged(ref _runOnStartup, value);
    }

    private bool _autoCheckUpdates;
    public bool AutoCheckUpdates
    {
        get => _autoCheckUpdates;
        set => this.RaiseAndSetIfChanged(ref _autoCheckUpdates, value);
    }

    // Snip Settings
    private double _borderThickness;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private double _maskOpacity;
    public double MaskOpacity
    {
        get => _maskOpacity;
        set => this.RaiseAndSetIfChanged(ref _maskOpacity, value);
    }

    private double _wingScale;
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

    private double _cornerIconScale = 1.0;
    public double CornerIconScale
    {
        get => _cornerIconScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _cornerIconScale, value);
            this.RaisePropertyChanged(nameof(PreviewIconSize));
        }
    }

    public double PreviewIconSize => 28 * CornerIconScale;
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
                UpdateThemeResources(value);
                this.RaisePropertyChanged(nameof(ThemeDeepColor));
            }
        }
    }

    public Color ThemeDeepColor 
    {
        get
        {
            if (ThemeColor == Color.Parse("#D4AF37")) return Color.Parse("#8B7500");
            if (ThemeColor == Color.Parse("#E0E0E0")) return Color.Parse("#606060");
            return Color.Parse("#900000");
        }
    }

    // Output Settings
    private bool _autoSave;
    public bool AutoSave
    {
        get => _autoSave;
        set => this.RaiseAndSetIfChanged(ref _autoSave, value);
    }
    
    private string _saveDirectory = string.Empty;
    public string SaveDirectory
    {
        get => _saveDirectory;
        set => this.RaiseAndSetIfChanged(ref _saveDirectory, value);
    }
    
    // Control Settings
    private string _snipHotkey = "F1";
    public string SnipHotkey
    {
        get => _snipHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _snipHotkey, value);
            HotkeyService.Register(ID_SNIP, value);
            this.RaisePropertyChanged(nameof(SnipTooltip));
        }
    }

    private string _copyHotkey = "Ctrl+C";
    public string CopyHotkey
    {
        get => _copyHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _copyHotkey, value);
            this.RaisePropertyChanged(nameof(CopyTooltip));
        }
    }

    private string _pinHotkey = "F3";
    public string PinHotkey
    {
        get => _pinHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _pinHotkey, value);
            this.RaisePropertyChanged(nameof(PinTooltip));
        }
    }

    private string _recordHotkey = "F2";
    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _recordHotkey, value);
            HotkeyService.Register(ID_RECORD, value);
            this.RaisePropertyChanged(nameof(RecordTooltip));
        }
    }

    public string SnipTooltip => $"{LocalizationService.Instance["StartCapture"]} ({SnipHotkey})";
    public string RecordTooltip => $"{LocalizationService.Instance["CaptureModeRecord"]} ({RecordHotkey})";
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => $"{LocalizationService.Instance["TipPin"]} ({PinHotkey})";

    // Drawing Tool Hotkeys
    private string _rectangleHotkey = "R";
    public string RectangleHotkey
    {
        get => _rectangleHotkey;
        set => this.RaiseAndSetIfChanged(ref _rectangleHotkey, value);
    }

    private string _ellipseHotkey = "E";
    public string EllipseHotkey
    {
        get => _ellipseHotkey;
        set => this.RaiseAndSetIfChanged(ref _ellipseHotkey, value);
    }

    private string _arrowHotkey = "A";
    public string ArrowHotkey
    {
        get => _arrowHotkey;
        set => this.RaiseAndSetIfChanged(ref _arrowHotkey, value);
    }

    private string _lineHotkey = "L";
    public string LineHotkey
    {
        get => _lineHotkey;
        set => this.RaiseAndSetIfChanged(ref _lineHotkey, value);
    }

    private string _penHotkey = "P";
    public string PenHotkey
    {
        get => _penHotkey;
        set => this.RaiseAndSetIfChanged(ref _penHotkey, value);
    }

    private string _textHotkey = "T";
    public string TextHotkey
    {
        get => _textHotkey;
        set => this.RaiseAndSetIfChanged(ref _textHotkey, value);
    }

    private string _mosaicHotkey = "M";
    public string MosaicHotkey
    {
        get => _mosaicHotkey;
        set => this.RaiseAndSetIfChanged(ref _mosaicHotkey, value);
    }

    private string _blurHotkey = "B";
    public string BlurHotkey
    {
        get => _blurHotkey;
        set => this.RaiseAndSetIfChanged(ref _blurHotkey, value);
    }

    // Action Hotkeys
    private string _undoHotkey = "Ctrl+Z";
    public string UndoHotkey
    {
        get => _undoHotkey;
        set => this.RaiseAndSetIfChanged(ref _undoHotkey, value);
    }

    private string _redoHotkey = "Ctrl+Y";
    public string RedoHotkey
    {
        get => _redoHotkey;
        set => this.RaiseAndSetIfChanged(ref _redoHotkey, value);
    }

    private string _clearHotkey = "Delete";
    public string ClearHotkey
    {
        get => _clearHotkey;
        set => this.RaiseAndSetIfChanged(ref _clearHotkey, value);
    }

    private string _saveHotkey = "Ctrl+S";
    public string SaveHotkey
    {
        get => _saveHotkey;
        set => this.RaiseAndSetIfChanged(ref _saveHotkey, value);
    }

    private string _closeHotkey = "Escape";
    public string CloseHotkey
    {
        get => _closeHotkey;
        set => this.RaiseAndSetIfChanged(ref _closeHotkey, value);
    }

    private string _togglePlaybackHotkey = "Space";
    public string TogglePlaybackHotkey
    {
        get => _togglePlaybackHotkey;
        set => this.RaiseAndSetIfChanged(ref _togglePlaybackHotkey, value);
    }

    private string _toggleToolbarHotkey = "F4";
    public string ToggleToolbarHotkey
    {
        get => _toggleToolbarHotkey;
        set => this.RaiseAndSetIfChanged(ref _toggleToolbarHotkey, value);
    }

    private string _selectionModeHotkey = "S";
    public string SelectionModeHotkey
    {
        get => _selectionModeHotkey;
        set => this.RaiseAndSetIfChanged(ref _selectionModeHotkey, value);
    }

    private string _cropModeHotkey = "C";
    public string CropModeHotkey
    {
        get => _cropModeHotkey;
        set => this.RaiseAndSetIfChanged(ref _cropModeHotkey, value);
    }

    private string _videoSaveDirectory = string.Empty;
    public string VideoSaveDirectory
    {
        get => _videoSaveDirectory;
        set => this.RaiseAndSetIfChanged(ref _videoSaveDirectory, value);
    }

    private string _recordFormat = "gif";
    public string RecordFormat
    {
        get => _recordFormat;
        set => this.RaiseAndSetIfChanged(ref _recordFormat, value);
    }

    private int _recordFPS = 30;
    public int RecordFPS
    {
        get => _recordFPS;
        set => this.RaiseAndSetIfChanged(ref _recordFPS, value);
    }

    private VideoCodec _videoCodec = VideoCodec.H264;
    public VideoCodec VideoCodec
    {
        get => _videoCodec;
        set => this.RaiseAndSetIfChanged(ref _videoCodec, value);
    }

    public class VideoCodecOption
    {
        public VideoCodec Value { get; set; }
        public string Name => LocalizationService.Instance[$"VideoCodec{Value}"];
    }

    public VideoCodecOption[] VideoCodecOptions { get; } = {
        new VideoCodecOption { Value = VideoCodec.H264 },
        new VideoCodecOption { Value = VideoCodec.H265 }
    };

    private VideoCodecOption? _selectedVideoCodecOption;
    public VideoCodecOption? SelectedVideoCodecOption
    {
        get => _selectedVideoCodecOption;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectedVideoCodecOption, value);
            if (value != null) VideoCodec = value.Value;
        }
    }

    private bool _useFixedRecordPath;
    public bool UseFixedRecordPath
    {
        get => _useFixedRecordPath;
        set => this.RaiseAndSetIfChanged(ref _useFixedRecordPath, value);
    }

    private bool _hideSnipPinDecoration = false;
    public bool HideSnipPinDecoration
    {
        get => _hideSnipPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideSnipPinDecoration, value);
    }

    private bool _hideSnipPinBorder = false;
    public bool HideSnipPinBorder
    {
        get => _hideSnipPinBorder;
        set => this.RaiseAndSetIfChanged(ref _hideSnipPinBorder, value);
    }
    
    private bool _defaultHideSnipToolbar = false;
    public bool DefaultHideSnipToolbar
    {
        get => _defaultHideSnipToolbar;
        set => this.RaiseAndSetIfChanged(ref _defaultHideSnipToolbar, value);
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

    private bool _hideSnipSelectionDecoration = false;
    public bool HideSnipSelectionDecoration
    {
        get => _hideSnipSelectionDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideSnipSelectionDecoration, value);
    }

    private bool _hideSnipSelectionBorder = false;
    public bool HideSnipSelectionBorder
    {
        get => _hideSnipSelectionBorder;
        set => this.RaiseAndSetIfChanged(ref _hideSnipSelectionBorder, value);
    }

    private bool _hideRecordSelectionDecoration = false;
    public bool HideRecordSelectionDecoration
    {
        get => _hideRecordSelectionDecoration;
        set => this.RaiseAndSetIfChanged(ref _hideRecordSelectionDecoration, value);
    }

    private bool _hideRecordSelectionBorder = false;
    public bool HideRecordSelectionBorder
    {
        get => _hideRecordSelectionBorder;
        set => this.RaiseAndSetIfChanged(ref _hideRecordSelectionBorder, value);
    }

    private string _tempDirectory = string.Empty;
    public string TempDirectory
    {
        get => _tempDirectory;
        set => this.RaiseAndSetIfChanged(ref _tempDirectory, value);
    }

    private bool _showSnipCursor = false;
    public bool ShowSnipCursor
    {
        get => _showSnipCursor;
        set => this.RaiseAndSetIfChanged(ref _showSnipCursor, value);
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
                if (!_isDataLoading)
                {
                    _ = SaveSettingsAsync();
                }
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
                _ = SaveSettingsAsync();
            }
        }
    }

    private int _sam2GridDensity = 8;
    public int SAM2GridDensity
    {
        get => _sam2GridDensity;
        set => this.RaiseAndSetIfChanged(ref _sam2GridDensity, value);
    }

    private int _sam2MaxObjects = 20;
    public int SAM2MaxObjects
    {
        get => _sam2MaxObjects;
        set => this.RaiseAndSetIfChanged(ref _sam2MaxObjects, value);
    }

    private int _sam2MinObjectSize = 20;
    public int SAM2MinObjectSize
    {
        get => _sam2MinObjectSize;
        set => this.RaiseAndSetIfChanged(ref _sam2MinObjectSize, value);
    }



    private string _ollamaModel = "";
    public string OllamaModel
    {
        get => _ollamaModel;
        set
        {
            _settingsService.DebugLog($"[Ollama] Setter called with: '{value}' (Current: '{_ollamaModel}', Loading: {_isDataLoading})");
            if (string.IsNullOrWhiteSpace(value)) 
            {
                // If UI tries to null it out (e.g. during binding reset), force it back to the current value
                if (!_isDataLoading) 
                {
                    _settingsService.DebugLog($"[Ollama] Rejecting empty value and notifying UI to revert to '{_ollamaModel}'");
                    this.RaisePropertyChanged(nameof(OllamaModel));
                }
                return; 
            }
            
            this.RaiseAndSetIfChanged(ref _ollamaModel, value);
            if (!_isDataLoading)
            {
                _settingsService.Settings.OllamaModel = value;
                IsModified = true;
                _ = SaveSettingsAsync();
            }
        }
    }

    private string _ollamaApiUrl = "http://localhost:11434/api/generate";
    public string OllamaApiUrl
    {
        get => _ollamaApiUrl;
        set
        {
            this.RaiseAndSetIfChanged(ref _ollamaApiUrl, value);
            if (!_isDataLoading)
            {
                _settingsService.Settings.OllamaApiUrl = value;
                IsModified = true;
                _ = SaveSettingsAsync();
            }
        }
    }


    public ObservableCollection<string> AvailableOllamaModels { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshOllamaModelsCommand { get; private set; }

    public async Task RefreshOllamaModelsAsync()
    {
        try
        {
            StatusText = "Refreshing Ollama Models...";
            string baseUrl = OllamaApiUrl.Replace("/api/generate", "");
            if (baseUrl.EndsWith("/")) baseUrl = baseUrl.TrimEnd('/');
            
            _settingsService.DebugLog($"[Ollama] Refreshing models from {baseUrl}...");
            
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var response = await client.GetStringAsync($"{baseUrl}/api/tags");
            
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                var names = new List<string>();
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        names.Add(name.GetString() ?? "");
                    }
                }
                
                var savedModel = _ollamaModel; 
                _settingsService.DebugLog($"[Ollama] API returned {names.Count} models. Current internal value is '{savedModel}'");

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    // Surgical update to avoid triggering ComboBox reset
                    var currentItems = AvailableOllamaModels.ToList();
                    
                    // Remove items not in the new list, but KEEP the currently selected one
                    foreach (var item in currentItems)
                    {
                        if (!names.Contains(item) && item != savedModel)
                        {
                            AvailableOllamaModels.Remove(item);
                        }
                    }

                    // Add new items
                    foreach (var name in names)
                    {
                        if (!AvailableOllamaModels.Contains(name))
                        {
                            AvailableOllamaModels.Add(name);
                        }
                    }

                    // Force ComboBox to re-evaluate by clearing then re-setting.
                    // Avalonia ComboBox won't update SelectedItem unless it sees a real change.
                    _ollamaModel = null!;
                    this.RaisePropertyChanged(nameof(OllamaModel));

                    if (!string.IsNullOrEmpty(savedModel) && AvailableOllamaModels.Contains(savedModel))
                    {
                        _ollamaModel = savedModel;
                    }
                    else if (AvailableOllamaModels.Count > 0)
                    {
                        _ollamaModel = AvailableOllamaModels[0];
                    }
                    else
                    {
                        _ollamaModel = savedModel ?? "";
                    }
                    
                    this.RaisePropertyChanged(nameof(OllamaModel));
                });
            }
            StatusText = "Ollama Models Refreshed";
        }
        catch (Exception ex)
        {
            _settingsService.DebugLog($"[Ollama] ERROR during refresh: {ex.Message}");
            StatusText = "Failed to refresh Ollama models";
            
            // Backup: Ensure the UI still knows about our loaded model
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
            {
                if (!string.IsNullOrEmpty(_ollamaModel))
                {
                    if (!AvailableOllamaModels.Contains(_ollamaModel))
                    {
                        AvailableOllamaModels.Add(_ollamaModel);
                    }
                    // Ensure _ollamaModel points to the collection instance
                    var match = AvailableOllamaModels.FirstOrDefault(m => m == _ollamaModel);
                    if (match != null) _ollamaModel = match;
                    this.RaisePropertyChanged(nameof(OllamaModel));
                }
            });
        }
    }

    private bool _showRecordCursor = true;
    public bool ShowRecordCursor
    {
        get => _showRecordCursor;
        set => this.RaiseAndSetIfChanged(ref _showRecordCursor, value);
    }

    public string[] AvailableRecordFormats { get; } = { "mp4", "mkv", "gif", "webm", "mov" };

    public async Task LoadSettingsAsync()
    {
        _isDataLoading = true;
        try
        {
            await _settingsService.LoadAsync();
            var settings = _settingsService.Settings;
            
            RunOnStartup = settings.RunOnStartup;
            AutoCheckUpdates = settings.AutoCheckUpdates;
            BorderThickness = settings.BorderThickness;
            MaskOpacity = settings.MaskOpacity;
            AutoSave = settings.AutoSave;
            SaveDirectory = settings.SaveDirectory;
            SnipHotkey = settings.SnipHotkey;
            CopyHotkey = settings.CopyHotkey;
            PinHotkey = settings.PinHotkey;
            RecordHotkey = settings.RecordHotkey;
            RecordFormat = settings.RecordFormat;
            VideoSaveDirectory = settings.VideoSaveDirectory;
            VideoCodec = settings.VideoCodec;
            UseFixedRecordPath = settings.UseFixedRecordPath;
            HideSnipPinDecoration = settings.HideSnipPinDecoration;
            HideSnipPinBorder = settings.HideSnipPinBorder;
            DefaultHideSnipToolbar = settings.DefaultHideSnipToolbar;
            DefaultHideRecordToolbar = settings.DefaultHideRecordToolbar;
            HideRecordPinDecoration = settings.HideRecordPinDecoration;
            HideRecordPinBorder = settings.HideRecordPinBorder;
            HideSnipSelectionDecoration = settings.HideSnipSelectionDecoration;
            HideSnipSelectionBorder = settings.HideSnipSelectionBorder;
            HideRecordSelectionDecoration = settings.HideRecordSelectionDecoration;
            HideRecordSelectionBorder = settings.HideRecordSelectionBorder;
            ShowSnipCursor = settings.ShowSnipCursor;
            ShowRecordCursor = settings.ShowRecordCursor;
            TempDirectory = settings.TempDirectory;
            ShowAIScanBox = settings.ShowAIScanBox;
            EnableAI = settings.EnableAI;
            SAM2GridDensity = settings.SAM2GridDensity;
            SAM2MaxObjects = settings.SAM2MaxObjects;
            SAM2MinObjectSize = settings.SAM2MinObjectSize;
            WingScale = settings.WingScale;
            CornerIconScale = settings.CornerIconScale;
            RecordFPS = settings.RecordFPS;
            EnableAIScan = settings.EnableAIScan;
            AIResourcesDirectory = settings.AIResourcesDirectory;
            OllamaApiUrl = settings.OllamaApiUrl;
            SelectedTranslationEngine = settings.SelectedTranslationEngine;
            OllamaModel = settings.OllamaModel;
            SourceLanguage = settings.SourceLanguage;
            TargetLanguage = settings.TargetLanguage;
            
            // Seed the list so ComboBox can show the value immediately.
            if (!string.IsNullOrEmpty(settings.OllamaModel))
            {
                if (!AvailableOllamaModels.Contains(settings.OllamaModel))
                    AvailableOllamaModels.Add(settings.OllamaModel);
            }

            if (Color.TryParse(settings.BorderColorHex, out var color))
                BorderColor = color;
                
            if (Color.TryParse(settings.ThemeColorHex, out var themeColor))
                ThemeColor = themeColor;

            SelectedLanguageOption = AvailableLanguages.FirstOrDefault(x => x.Value == settings.Language) ?? AvailableLanguages[0];
            SelectedVideoCodecOption = VideoCodecOptions.FirstOrDefault(x => x.Value == settings.VideoCodec);
            
            this.RaisePropertyChanged(nameof(SourceLanguage));
            this.RaisePropertyChanged(nameof(TargetLanguage));

            IsModified = false;

        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
        }
        finally
        {
            _isDataLoading = false;
            InitializeModules();

            // Force ComboBox to pick up OllamaModel by toggling null -> value.
            // This must happen AFTER _isDataLoading=false so the UI binding is active.
            var savedOllamaModel = _ollamaModel;
            _ollamaModel = null!;
            this.RaisePropertyChanged(nameof(OllamaModel));
            _ollamaModel = savedOllamaModel;
            this.RaisePropertyChanged(nameof(OllamaModel));

            if (AutoCheckUpdates) _ = CheckForUpdates(true);
            _ = RefreshOllamaModelsAsync(); // Load settings first, THEN refresh models
        }
    }

    public async Task<bool> SaveSettingsAsync()
    {
        if (_isDataLoading) return false;

        try
        {
            var settings = _settingsService.Settings;
            settings.RunOnStartup = RunOnStartup;
            settings.AutoCheckUpdates = AutoCheckUpdates;
            settings.BorderThickness = BorderThickness;
            settings.MaskOpacity = MaskOpacity;
            settings.AutoSave = AutoSave;
            settings.SaveDirectory = SaveDirectory;
            settings.SnipHotkey = SnipHotkey;
            settings.CopyHotkey = CopyHotkey;
            settings.PinHotkey = PinHotkey;
            settings.RecordHotkey = RecordHotkey;
            settings.RecordFormat = RecordFormat;
            settings.VideoSaveDirectory = VideoSaveDirectory;
            settings.VideoCodec = VideoCodec;
            settings.UseFixedRecordPath = UseFixedRecordPath;
            settings.HideSnipPinDecoration = HideSnipPinDecoration;
            settings.HideSnipPinBorder = HideSnipPinBorder;
            settings.DefaultHideSnipToolbar = DefaultHideSnipToolbar;
            settings.DefaultHideRecordToolbar = DefaultHideRecordToolbar;
            settings.HideRecordPinDecoration = HideRecordPinDecoration;
            settings.HideRecordPinBorder = HideRecordPinBorder;
            settings.HideSnipSelectionDecoration = HideSnipSelectionDecoration;
            settings.HideSnipSelectionBorder = HideSnipSelectionBorder;
            settings.HideRecordSelectionDecoration = HideRecordSelectionDecoration;
            settings.HideRecordSelectionBorder = HideRecordSelectionBorder;
            settings.ShowSnipCursor = ShowSnipCursor;
            settings.ShowRecordCursor = ShowRecordCursor;
            settings.TempDirectory = TempDirectory;
            settings.ShowAIScanBox = ShowAIScanBox;
            settings.EnableAI = EnableAI;
            settings.SAM2GridDensity = SAM2GridDensity;
            settings.SAM2MaxObjects = SAM2MaxObjects;
            settings.SAM2MinObjectSize = SAM2MinObjectSize;
            settings.WingScale = WingScale;
            settings.CornerIconScale = CornerIconScale;
            settings.RecordFPS = RecordFPS;
            settings.TargetLanguage = TargetLanguage;
            settings.SourceLanguage = SourceLanguage;
            settings.OllamaModel = OllamaModel;
            settings.OllamaApiUrl = OllamaApiUrl;
            settings.SelectedTranslationEngine = SelectedTranslationEngine;
            settings.BorderColorHex = BorderColor.ToString();
            settings.ThemeColorHex = ThemeColor.ToString();
            settings.Language = SelectedLanguageOption.Value;


            await _settingsService.SaveAsync();
            IsModified = false;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            return false;
        }
    }

    private void UpdateThemeResources(Color themeColor)
    {
        if (Application.Current?.Resources is { } resources)
        {
            resources["ThemeAccentColor"] = themeColor;
            resources["ThemeDeepColor"] = ThemeDeepColor;
        }
    }
}
