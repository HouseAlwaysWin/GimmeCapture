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
using GimmeCapture.Services.Core.Infrastructure;
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
                   _settingsService.Settings.SourceLanguage = value; // Immediate sync
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
                   _settingsService.Settings.TargetLanguage = value; // Immediate sync
                   IsModified = true; // Mark as modified so Save can happen if auto-save or manual save
                   _ = SaveSettingsAsync(); // Auto-save for convenience
                }
            }
        }
    }

    public List<TranslationEngine> AvailableTranslationEngines { get; } = Enum.GetValues<TranslationEngine>().ToList();
    public List<AIScanEngine> AvailableAIScanEngines { get; } = Enum.GetValues<AIScanEngine>().ToList();
    
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
                    _settingsService.Settings.SelectedTranslationEngine = value; // Immediate sync
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
            HotkeyService.Register(HotkeyIds.Snip, value);
            this.RaisePropertyChanged(nameof(SnipTooltip));
            if (!_isDataLoading)
            {
                _settingsService.Settings.SnipHotkey = value;
                IsModified = true;
                _ = SaveSettingsAsync();
            }
        }
    }


    private string _translateHotkey = "F3";
    public string TranslateHotkey
    {
        get => _translateHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _translateHotkey, value);
            HotkeyService.Register(HotkeyIds.Translate, value);
            this.RaisePropertyChanged(nameof(TranslateTooltip));
            if (!_isDataLoading)
            {
                _settingsService.Settings.TranslateHotkey = value;
                IsModified = true;
                _ = SaveSettingsAsync();
            }
        }
    }

    private string _recordHotkey = "F2";
    public string RecordHotkey
    {
        get => _recordHotkey;
        set
        {
            this.RaiseAndSetIfChanged(ref _recordHotkey, value);
            HotkeyService.Register(HotkeyIds.Record, value);
            this.RaisePropertyChanged(nameof(RecordTooltip));
            if (!_isDataLoading)
            {
                _settingsService.Settings.RecordHotkey = value;
                IsModified = true;
                _ = SaveSettingsAsync();
            }
        }
    }

    public string SnipTooltip => $"{LocalizationService.Instance["StartCapture"]} ({SnipHotkey})";
    public string RecordTooltip => $"{LocalizationService.Instance["CaptureModeRecord"]} ({RecordHotkey})";
    public string TranslateTooltip => $"{LocalizationService.Instance["TranslateHotkey"]} ({TranslateHotkey})";

    // Snip Mode Hotkeys
    public string Snip_Rectangle { get => _settingsService.Settings.Snip.Rectangle; set { if (_settingsService.Settings.Snip.Rectangle != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Rectangle = value; this.RaisePropertyChanged(); } } }
    public string Snip_Ellipse { get => _settingsService.Settings.Snip.Ellipse; set { if (_settingsService.Settings.Snip.Ellipse != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Ellipse = value; this.RaisePropertyChanged(); } } }
    public string Snip_Arrow { get => _settingsService.Settings.Snip.Arrow; set { if (_settingsService.Settings.Snip.Arrow != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Arrow = value; this.RaisePropertyChanged(); } } }
    public string Snip_Line { get => _settingsService.Settings.Snip.Line; set { if (_settingsService.Settings.Snip.Line != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Line = value; this.RaisePropertyChanged(); } } }
    public string Snip_Pen { get => _settingsService.Settings.Snip.Pen; set { if (_settingsService.Settings.Snip.Pen != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Pen = value; this.RaisePropertyChanged(); } } }
    public string Snip_Text { get => _settingsService.Settings.Snip.Text; set { if (_settingsService.Settings.Snip.Text != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Text = value; this.RaisePropertyChanged(); } } }
    public string Snip_Mosaic { get => _settingsService.Settings.Snip.Mosaic; set { if (_settingsService.Settings.Snip.Mosaic != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Mosaic = value; this.RaisePropertyChanged(); } } }
    public string Snip_Blur { get => _settingsService.Settings.Snip.Blur; set { if (_settingsService.Settings.Snip.Blur != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Blur = value; this.RaisePropertyChanged(); } } }
    public string Snip_Undo { get => _settingsService.Settings.Snip.Undo; set { if (_settingsService.Settings.Snip.Undo != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Undo = value; this.RaisePropertyChanged(); } } }
    public string Snip_Redo { get => _settingsService.Settings.Snip.Redo; set { if (_settingsService.Settings.Snip.Redo != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Redo = value; this.RaisePropertyChanged(); } } }
    public string Snip_Clear { get => _settingsService.Settings.Snip.Clear; set { if (_settingsService.Settings.Snip.Clear != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Clear = value; this.RaisePropertyChanged(); } } }
    public string Snip_Save { get => _settingsService.Settings.Snip.Save; set { if (_settingsService.Settings.Snip.Save != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Save = value; this.RaisePropertyChanged(); } } }
    public string Snip_Copy { get => _settingsService.Settings.Snip.Copy; set { if (_settingsService.Settings.Snip.Copy != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Copy = value; this.RaisePropertyChanged(); } } }
    public string Snip_Pin { get => _settingsService.Settings.Snip.Pin; set { if (_settingsService.Settings.Snip.Pin != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Pin = value; this.RaisePropertyChanged(); } } }
    public string Snip_Close { get => _settingsService.Settings.Snip.Close; set { if (_settingsService.Settings.Snip.Close != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Close = value; this.RaisePropertyChanged(); } } }
    public string Snip_Toolbar { get => _settingsService.Settings.Snip.Toolbar; set { if (_settingsService.Settings.Snip.Toolbar != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Toolbar = value; this.RaisePropertyChanged(); } } }
    public string Snip_SelectionMode { get => _settingsService.Settings.Snip.SelectionMode; set { if (_settingsService.Settings.Snip.SelectionMode != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.SelectionMode = value; this.RaisePropertyChanged(); } } }
    public string Snip_CropMode { get => _settingsService.Settings.Snip.CropMode; set { if (_settingsService.Settings.Snip.CropMode != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.CropMode = value; this.RaisePropertyChanged(); } } }
    public string Snip_RemoveBackground { get => _settingsService.Settings.Snip.RemoveBackground; set { if (_settingsService.Settings.Snip.RemoveBackground != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.RemoveBackground = value; this.RaisePropertyChanged(); } } }
    public string Snip_MagicWand { get => _settingsService.Settings.Snip.MagicWand; set { if (_settingsService.Settings.Snip.MagicWand != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.MagicWand = value; this.RaisePropertyChanged(); } } }
    public string Snip_SwitchToTranslate { get => _settingsService.Settings.Snip.SwitchToTranslate; set { if (_settingsService.Settings.Snip.SwitchToTranslate != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.SwitchToTranslate = value; this.RaisePropertyChanged(); } } }
    public string Snip_SwitchToRecord { get => _settingsService.Settings.Snip.SwitchToRecord; set { if (_settingsService.Settings.Snip.SwitchToRecord != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.SwitchToRecord = value; this.RaisePropertyChanged(); } } }

    // Record Mode Hotkeys
    public string Record_Rectangle { get => _settingsService.Settings.Record.Rectangle; set { if (_settingsService.Settings.Record.Rectangle != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Rectangle = value; this.RaisePropertyChanged(); } } }
    public string Record_Ellipse { get => _settingsService.Settings.Record.Ellipse; set { if (_settingsService.Settings.Record.Ellipse != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Ellipse = value; this.RaisePropertyChanged(); } } }
    public string Record_Arrow { get => _settingsService.Settings.Record.Arrow; set { if (_settingsService.Settings.Record.Arrow != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Arrow = value; this.RaisePropertyChanged(); } } }
    public string Record_Line { get => _settingsService.Settings.Record.Line; set { if (_settingsService.Settings.Record.Line != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Line = value; this.RaisePropertyChanged(); } } }
    public string Record_Pen { get => _settingsService.Settings.Record.Pen; set { if (_settingsService.Settings.Record.Pen != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Pen = value; this.RaisePropertyChanged(); } } }
    public string Record_Text { get => _settingsService.Settings.Record.Text; set { if (_settingsService.Settings.Record.Text != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Text = value; this.RaisePropertyChanged(); } } }
    public string Record_Mosaic { get => _settingsService.Settings.Record.Mosaic; set { if (_settingsService.Settings.Record.Mosaic != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Mosaic = value; this.RaisePropertyChanged(); } } }
    public string Record_Blur { get => _settingsService.Settings.Record.Blur; set { if (_settingsService.Settings.Record.Blur != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Blur = value; this.RaisePropertyChanged(); } } }
    public string Record_Undo { get => _settingsService.Settings.Record.Undo; set { if (_settingsService.Settings.Record.Undo != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Undo = value; this.RaisePropertyChanged(); } } }
    public string Record_Redo { get => _settingsService.Settings.Record.Redo; set { if (_settingsService.Settings.Record.Redo != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Redo = value; this.RaisePropertyChanged(); } } }
    public string Record_Clear { get => _settingsService.Settings.Record.Clear; set { if (_settingsService.Settings.Record.Clear != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Clear = value; this.RaisePropertyChanged(); } } }
    public string Record_Save { get => _settingsService.Settings.Record.Save; set { if (_settingsService.Settings.Record.Save != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Save = value; this.RaisePropertyChanged(); } } }
    public string Record_Copy { get => _settingsService.Settings.Record.Copy; set { if (_settingsService.Settings.Record.Copy != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Copy = value; this.RaisePropertyChanged(); } } }
    public string Record_Close { get => _settingsService.Settings.Record.Close; set { if (_settingsService.Settings.Record.Close != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Close = value; this.RaisePropertyChanged(); } } }
    public string Record_Toolbar { get => _settingsService.Settings.Record.Toolbar; set { if (_settingsService.Settings.Record.Toolbar != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Toolbar = value; this.RaisePropertyChanged(); } } }
    public string Record_Action { get => _settingsService.Settings.Record.Action; set { if (_settingsService.Settings.Record.Action != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Action = value; this.RaisePropertyChanged(); } } }
    public string Record_Playback { get => _settingsService.Settings.Record.Playback; set { if (_settingsService.Settings.Record.Playback != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Playback = value; this.RaisePropertyChanged(); } } }
    public string Record_SwitchToSnip { get => _settingsService.Settings.Record.SwitchToSnip; set { if (_settingsService.Settings.Record.SwitchToSnip != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.SwitchToSnip = value; this.RaisePropertyChanged(); } } }
    public string Record_SwitchToTranslate { get => _settingsService.Settings.Record.SwitchToTranslate; set { if (_settingsService.Settings.Record.SwitchToTranslate != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.SwitchToTranslate = value; this.RaisePropertyChanged(); } } }

    // Translate Mode Hotkeys
    public string Translate_Action { get => _settingsService.Settings.Translate.Action; set { if (_settingsService.Settings.Translate.Action != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.Action = value; this.RaisePropertyChanged(); } } }
    public string Translate_Toolbar { get => _settingsService.Settings.Translate.Toolbar; set { if (_settingsService.Settings.Translate.Toolbar != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.Toolbar = value; this.RaisePropertyChanged(); } } }
    public string Translate_Close { get => _settingsService.Settings.Translate.Close; set { if (_settingsService.Settings.Translate.Close != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.Close = value; this.RaisePropertyChanged(); } } }
    public string Translate_TranslateAll { get => _settingsService.Settings.Translate.TranslateAll; set { if (_settingsService.Settings.Translate.TranslateAll != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.TranslateAll = value; this.RaisePropertyChanged(); } } }
    public string Translate_ScanAll { get => _settingsService.Settings.Translate.ScanAll; set { if (_settingsService.Settings.Translate.ScanAll != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ScanAll = value; this.RaisePropertyChanged(); } } }
    public string Translate_ClearAll { get => _settingsService.Settings.Translate.ClearAll; set { if (_settingsService.Settings.Translate.ClearAll != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ClearAll = value; this.RaisePropertyChanged(); } } }
    public string Translate_ToggleSelect { get => _settingsService.Settings.Translate.ToggleSelect; set { if (_settingsService.Settings.Translate.ToggleSelect != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ToggleSelect = value; this.RaisePropertyChanged(); } } }
    public string Translate_AutoDetect { get => _settingsService.Settings.Translate.AutoDetect; set { if (_settingsService.Settings.Translate.AutoDetect != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.AutoDetect = value; this.RaisePropertyChanged(); } } }
    public string Translate_HoldSingle { get => _settingsService.Settings.Translate.HoldSingle; set { if (_settingsService.Settings.Translate.HoldSingle != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.HoldSingle = value; this.RaisePropertyChanged(); } } }
    public string Translate_HoldMulti { get => _settingsService.Settings.Translate.HoldMulti; set { if (_settingsService.Settings.Translate.HoldMulti != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.HoldMulti = value; this.RaisePropertyChanged(); } } }
    public string Translate_SwitchToSnip { get => _settingsService.Settings.Translate.SwitchToSnip; set { if (_settingsService.Settings.Translate.SwitchToSnip != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.SwitchToSnip = value; this.RaisePropertyChanged(); } } }
    public string Translate_SwitchToRecord { get => _settingsService.Settings.Translate.SwitchToRecord; set { if (_settingsService.Settings.Translate.SwitchToRecord != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.SwitchToRecord = value; this.RaisePropertyChanged(); } } }
    public string Translate_ModeCursor { get => _settingsService.Settings.Translate.ModeCursor; set { if (_settingsService.Settings.Translate.ModeCursor != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ModeCursor = value; this.RaisePropertyChanged(); } } }
    public string Translate_ModeSingle { get => _settingsService.Settings.Translate.ModeSingle; set { if (_settingsService.Settings.Translate.ModeSingle != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ModeSingle = value; this.RaisePropertyChanged(); } } }
    public string Translate_ModeMulti { get => _settingsService.Settings.Translate.ModeMulti; set { if (_settingsService.Settings.Translate.ModeMulti != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ModeMulti = value; this.RaisePropertyChanged(); } } }

    public System.Collections.Generic.List<string> ModifierOptions { get; } = new() { "Shift", "Ctrl", "Alt", "None" };

    // Action Hotkeys

    private string _videoSaveDirectory = string.Empty;
    public string VideoSaveDirectory
    {
        get => _videoSaveDirectory;
        set => this.RaiseAndSetIfChanged(ref _videoSaveDirectory, value);
    }

    private string _recordFormat = "mp4";
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

    public string? CheckHotkeyConflict(string targetTag, string hotkey)
    {
        // 1. Global Group (Idle state triggers)
        var globalGroup = new[] { "SnipHotkey", "RecordHotkey", "TranslateHotkey", "PinHotkey", "CopyHotkey" };
        
        // 2. Snip Group (Local to Screenshot mode)
        var snipGroup = new[] { 
            "Snip_Rectangle", "Snip_Ellipse", "Snip_Arrow", "Snip_Line", "Snip_Pen", 
            "Snip_Text", "Snip_Mosaic", "Snip_Blur", "Snip_Undo", "Snip_Redo", 
            "Snip_Clear", "Snip_Save", "Snip_Copy", "Snip_Close", 
            "Snip_Toolbar", "Snip_SelectionMode", "Snip_CropMode", "Snip_Pin",
            "Snip_SwitchToTranslate", "Snip_SwitchToRecord"
        };

        // 3. Record Group (Local to Video Recording mode)
        var recordGroup = new[] { 
            "Record_Rectangle", "Record_Ellipse", "Record_Arrow", "Record_Line", "Record_Pen", 
            "Record_Text", "Record_Mosaic", "Record_Blur", "Record_Undo", "Record_Redo", 
            "Record_Clear", "Record_Save", "Record_Copy", "Record_Close", "Record_Toolbar", 
            "Record_Action", "Record_Playback",
            "Record_SwitchToSnip", "Record_SwitchToTranslate"
        };

        // 4. Translate Group (Local to Translation mode)
        var translateGroup = new[] { 
            "Translate_Action", "Translate_Toolbar", "Translate_Close",
            "Translate_TranslateAll", "Translate_ScanAll", "Translate_ClearAll",
            "Translate_ToggleSelect", "Translate_AutoDetect",
            "Translate_ModeCursor", "Translate_ModeSingle", "Translate_ModeMulti",
            "Translate_SwitchToSnip", "Translate_SwitchToRecord"
        };

        if (globalGroup.Contains(targetTag))
        {
            if (targetTag != "SnipHotkey" && SnipHotkey == hotkey) return "SnipHotkey";
            if (targetTag != "RecordHotkey" && RecordHotkey == hotkey) return "RecordHotkey";
            if (targetTag != "TranslateHotkey" && TranslateHotkey == hotkey) return "TranslateHotkey";
        }
        else if (snipGroup.Contains(targetTag))
        {
            if (targetTag != "Snip_Rectangle" && Snip_Rectangle == hotkey) return "TipRectangle";
            if (targetTag != "Snip_Ellipse" && Snip_Ellipse == hotkey) return "TipEllipse";
            if (targetTag != "Snip_Arrow" && Snip_Arrow == hotkey) return "TipArrow";
            if (targetTag != "Snip_Line" && Snip_Line == hotkey) return "TipLine";
            if (targetTag != "Snip_Pen" && Snip_Pen == hotkey) return "TipPen";
            if (targetTag != "Snip_Text" && Snip_Text == hotkey) return "TipText";
            if (targetTag != "Snip_Mosaic" && Snip_Mosaic == hotkey) return "TipMosaic";
            if (targetTag != "Snip_Blur" && Snip_Blur == hotkey) return "TipBlur";
            if (targetTag != "Snip_Undo" && Snip_Undo == hotkey) return "Undo";
            if (targetTag != "Snip_Redo" && Snip_Redo == hotkey) return "Redo";
            if (targetTag != "Snip_Clear" && Snip_Clear == hotkey) return "Clear";
            if (targetTag != "Snip_Save" && Snip_Save == hotkey) return "Save";
            if (targetTag != "Snip_Copy" && Snip_Copy == hotkey) return "TipCopy";
            if (targetTag != "Snip_Close" && Snip_Close == hotkey) return "ActionClose";
            if (targetTag != "Snip_Toolbar" && Snip_Toolbar == hotkey) return "ActionToolbar";
            if (targetTag != "Snip_SelectionMode" && Snip_SelectionMode == hotkey) return "ActionSelectionMode";
            if (targetTag != "Snip_CropMode" && Snip_CropMode == hotkey) return "ActionCropMode";
            if (targetTag != "Snip_RemoveBackground" && Snip_RemoveBackground == hotkey) return "RemoveBackground";
            if (targetTag != "Snip_MagicWand" && Snip_MagicWand == hotkey) return "MagicWand";
            if (targetTag != "Snip_Pin" && Snip_Pin == hotkey) return "TipPin";
            if (targetTag != "Snip_SwitchToTranslate" && Snip_SwitchToTranslate == hotkey) return "SwitchToTranslate";
            if (targetTag != "Snip_SwitchToRecord" && Snip_SwitchToRecord == hotkey) return "SwitchToRecord";
        }
        else if (recordGroup.Contains(targetTag))
        {
            if (targetTag != "Record_Rectangle" && Record_Rectangle == hotkey) return "TipRectangle";
            if (targetTag != "Record_Ellipse" && Record_Ellipse == hotkey) return "TipEllipse";
            if (targetTag != "Record_Arrow" && Record_Arrow == hotkey) return "TipArrow";
            if (targetTag != "Record_Line" && Record_Line == hotkey) return "TipLine";
            if (targetTag != "Record_Pen" && Record_Pen == hotkey) return "TipPen";
            if (targetTag != "Record_Text" && Record_Text == hotkey) return "TipText";
            if (targetTag != "Record_Mosaic" && Record_Mosaic == hotkey) return "TipMosaic";
            if (targetTag != "Record_Blur" && Record_Blur == hotkey) return "TipBlur";
            if (targetTag != "Record_Undo" && Record_Undo == hotkey) return "Undo";
            if (targetTag != "Record_Redo" && Record_Redo == hotkey) return "Redo";
            if (targetTag != "Record_Clear" && Record_Clear == hotkey) return "Clear";
            if (targetTag != "Record_Save" && Record_Save == hotkey) return "Save";
            if (targetTag != "Record_Copy" && Record_Copy == hotkey) return "TipCopy";
            if (targetTag != "Record_Close" && Record_Close == hotkey) return "ActionClose";
            if (targetTag != "Record_Toolbar" && Record_Toolbar == hotkey) return "ActionToolbar";
            if (targetTag != "Record_Action" && Record_Action == hotkey) return "ActionStartPin";
            if (targetTag != "Record_Playback" && Record_Playback == hotkey) return "ActionPlayback";
            if (targetTag != "Record_SwitchToSnip" && Record_SwitchToSnip == hotkey) return "SwitchToSnip";
            if (targetTag != "Record_SwitchToTranslate" && Record_SwitchToTranslate == hotkey) return "SwitchToTranslate";
        }
        else if (translateGroup.Contains(targetTag))
        {
            if (targetTag != "Translate_Action" && Translate_Action == hotkey) return "ActionHideTranslate";
            if (targetTag != "Translate_Toolbar" && Translate_Toolbar == hotkey) return "ActionToolbar";
            if (targetTag != "Translate_Close" && Translate_Close == hotkey) return "ActionClose";
            if (targetTag != "Translate_TranslateAll" && Translate_TranslateAll == hotkey) return "ActionTranslateAll";
            if (targetTag != "Translate_ScanAll" && Translate_ScanAll == hotkey) return "ActionScanAll";
            if (targetTag != "Translate_ClearAll" && Translate_ClearAll == hotkey) return "ActionClearAll";
            if (targetTag != "Translate_ToggleSelect" && Translate_ToggleSelect == hotkey) return "ActionToggleSelect";
            if (targetTag != "Translate_AutoDetect" && Translate_AutoDetect == hotkey) return "ActionAutoDetect";
            if (targetTag != "Translate_HoldSingle" && Translate_HoldSingle == hotkey) return "ActionHoldSingle";
            if (targetTag != "Translate_HoldMulti" && Translate_HoldMulti == hotkey) return "ActionHoldMulti";
            if (targetTag != "Translate_ModeCursor" && Translate_ModeCursor == hotkey) return "TranslateModeCursor";
            if (targetTag != "Translate_ModeSingle" && Translate_ModeSingle == hotkey) return "TranslateModeSingle";
            if (targetTag != "Translate_ModeMulti" && Translate_ModeMulti == hotkey) return "TranslateModeMulti";
            if (targetTag != "Translate_SwitchToSnip" && Translate_SwitchToSnip == hotkey) return "SwitchToSnip";
            if (targetTag != "Translate_SwitchToRecord" && Translate_SwitchToRecord == hotkey) return "SwitchToRecord";
        }

        return null;
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

    public AIScanEngine AIScanEngine
    {
        get => _settingsService.Settings.AIScanEngine;
        set
        {
            if (_settingsService.Settings.AIScanEngine != value)
            {
                _settingsService.Settings.AIScanEngine = value;
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

                // 使用者關閉 AI 時，主動釋放 SAM2 模型記憶體
                if (!value)
                {
                    AIResourceService.UnloadSAM2Models();
                }
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
                _settingsService.Settings.OllamaModel = value; // Immediate sync
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
                _settingsService.Settings.OllamaApiUrl = value; // Immediate sync
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

    private bool _recordSystemAudio = true;
    public bool RecordSystemAudio
    {
        get => _recordSystemAudio;
        set => this.RaiseAndSetIfChanged(ref _recordSystemAudio, value);
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
            TranslateHotkey = settings.TranslateHotkey;
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
            RecordSystemAudio = settings.RecordSystemAudio;
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
            AIScanEngine = settings.AIScanEngine;
            AIResourcesDirectory = settings.AIResourcesDirectory;
            OllamaApiUrl = settings.OllamaApiUrl;
            SelectedTranslationEngine = settings.SelectedTranslationEngine;
            OllamaModel = settings.OllamaModel;
            SourceLanguage = settings.SourceLanguage;
            TargetLanguage = settings.TargetLanguage;

            // Trigger notifications for all hotkeys (they access Settings directly now)
            var hotkeyProps = new[] {
                nameof(Snip_Rectangle), nameof(Snip_Ellipse), nameof(Snip_Arrow), nameof(Snip_Line), nameof(Snip_Pen),
                nameof(Snip_Text), nameof(Snip_Mosaic), nameof(Snip_Blur), nameof(Snip_Undo), nameof(Snip_Redo),
                nameof(Snip_Clear), nameof(Snip_Save), nameof(Snip_Copy), nameof(Snip_Pin), nameof(Snip_Close),
                nameof(Snip_Toolbar), nameof(Snip_SelectionMode), nameof(Snip_CropMode), nameof(Snip_RemoveBackground),
                nameof(Record_Rectangle), nameof(Record_Ellipse), nameof(Record_Arrow), nameof(Record_Line), nameof(Record_Pen),
                nameof(Record_Text), nameof(Record_Mosaic), nameof(Record_Blur), nameof(Record_Undo), nameof(Record_Redo),
                nameof(Record_Clear), nameof(Record_Save), nameof(Record_Copy), nameof(Record_Close), nameof(Record_Toolbar),
                nameof(Record_Action), nameof(Record_Playback),
                nameof(Record_SwitchToSnip), nameof(Record_SwitchToTranslate),
                nameof(Translate_Action), nameof(Translate_Toolbar), nameof(Translate_Close),
                nameof(Translate_TranslateAll), nameof(Translate_ScanAll), nameof(Translate_ClearAll),
                nameof(Translate_ToggleSelect), nameof(Translate_AutoDetect),
                nameof(Translate_SwitchToSnip), nameof(Translate_SwitchToRecord),
                nameof(Snip_SwitchToTranslate), nameof(Snip_SwitchToRecord)
            };
            foreach (var prop in hotkeyProps) this.RaisePropertyChanged(prop);

            
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
            this.RaisePropertyChanged(nameof(AIScanEngine));

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
            settings.TranslateHotkey = TranslateHotkey;
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
            settings.RecordSystemAudio = RecordSystemAudio;
            settings.TempDirectory = TempDirectory;
            settings.ShowAIScanBox = ShowAIScanBox;
            settings.EnableAI = EnableAI;
            settings.SAM2GridDensity = SAM2GridDensity;
            settings.SAM2MaxObjects = SAM2MaxObjects;
            settings.SAM2MinObjectSize = SAM2MinObjectSize;
            settings.WingScale = WingScale;
            settings.CornerIconScale = CornerIconScale;
            settings.RecordFPS = RecordFPS;
            settings.AIScanEngine = AIScanEngine;
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
        _themeResourceService.UpdateThemeColors(themeColor, ThemeDeepColor);
    }
}
