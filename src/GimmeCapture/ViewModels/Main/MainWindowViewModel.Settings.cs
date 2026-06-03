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

    private static bool IsUnifiedPinHotkeyTag(string tag)
    {
        return tag == "Snip_Pin"
            || tag == "Record_Action"
            || tag == "Translate_Pin";
    }

    private void SetUnifiedPinHotkeys(string value)
    {
        bool snipChanged = _settingsService.Settings.Snip.Pin != value;
        bool recordChanged = _settingsService.Settings.Record.Action != value;
        bool translateChanged = _settingsService.Settings.Translate.Pin != value;

        if (!snipChanged && !recordChanged && !translateChanged)
        {
            return;
        }

        if (snipChanged)
        {
            this.RaisePropertyChanging(nameof(Snip_Pin));
            _settingsService.Settings.Snip.Pin = value;
            this.RaisePropertyChanged(nameof(Snip_Pin));
        }

        if (recordChanged)
        {
            this.RaisePropertyChanging(nameof(Record_Action));
            _settingsService.Settings.Record.Action = value;
            this.RaisePropertyChanged(nameof(Record_Action));
        }

        if (translateChanged)
        {
            this.RaisePropertyChanging(nameof(Translate_Pin));
            _settingsService.Settings.Translate.Pin = value;
            this.RaisePropertyChanged(nameof(Translate_Pin));
        }
    }

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
    public string Snip_Pin { get => _settingsService.Settings.Snip.Pin; set => SetUnifiedPinHotkeys(value); }
    public string Snip_Close { get => _settingsService.Settings.Snip.Close; set { if (_settingsService.Settings.Snip.Close != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Close = value; this.RaisePropertyChanged(); } } }
    public string Snip_Toolbar { get => _settingsService.Settings.Snip.Toolbar; set { if (_settingsService.Settings.Snip.Toolbar != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.Toolbar = value; this.RaisePropertyChanged(); } } }
    public string Snip_SelectionMode { get => _settingsService.Settings.Snip.SelectionMode; set { if (_settingsService.Settings.Snip.SelectionMode != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.SelectionMode = value; this.RaisePropertyChanged(); } } }
    public string Snip_CropMode { get => _settingsService.Settings.Snip.CropMode; set { if (_settingsService.Settings.Snip.CropMode != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.CropMode = value; this.RaisePropertyChanged(); } } }
    public string Snip_RemoveBackground { get => _settingsService.Settings.Snip.RemoveBackground; set { if (_settingsService.Settings.Snip.RemoveBackground != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.RemoveBackground = value; this.RaisePropertyChanged(); } } }
    public string Snip_MagicWand { get => _settingsService.Settings.Snip.MagicWand; set { if (_settingsService.Settings.Snip.MagicWand != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.MagicWand = value; this.RaisePropertyChanged(); } } }
    public string Snip_FullscreenSelect { get => _settingsService.Settings.Snip.FullscreenSelect; set { if (_settingsService.Settings.Snip.FullscreenSelect != value) { this.RaisePropertyChanging(); _settingsService.Settings.Snip.FullscreenSelect = value; this.RaisePropertyChanged(); } } }
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
    public string Record_Action { get => _settingsService.Settings.Record.Action; set => SetUnifiedPinHotkeys(value); }
    public string Record_Playback { get => _settingsService.Settings.Record.Playback; set { if (_settingsService.Settings.Record.Playback != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.Playback = value; this.RaisePropertyChanged(); } } }
    public string Record_FullscreenSelect { get => _settingsService.Settings.Record.FullscreenSelect; set { if (_settingsService.Settings.Record.FullscreenSelect != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.FullscreenSelect = value; this.RaisePropertyChanged(); } } }
    public string Record_SwitchToSnip { get => _settingsService.Settings.Record.SwitchToSnip; set { if (_settingsService.Settings.Record.SwitchToSnip != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.SwitchToSnip = value; this.RaisePropertyChanged(); } } }
    public string Record_SwitchToTranslate { get => _settingsService.Settings.Record.SwitchToTranslate; set { if (_settingsService.Settings.Record.SwitchToTranslate != value) { this.RaisePropertyChanging(); _settingsService.Settings.Record.SwitchToTranslate = value; this.RaisePropertyChanged(); } } }

    // Translate Mode Hotkeys
    public string Translate_Action { get => _settingsService.Settings.Translate.Action; set { if (_settingsService.Settings.Translate.Action != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.Action = value; this.RaisePropertyChanged(); } } }
    public string Translate_Pin { get => _settingsService.Settings.Translate.Pin; set => SetUnifiedPinHotkeys(value); }
    public string Translate_Toolbar { get => _settingsService.Settings.Translate.Toolbar; set { if (_settingsService.Settings.Translate.Toolbar != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.Toolbar = value; this.RaisePropertyChanged(); } } }
    public string Translate_Close { get => _settingsService.Settings.Translate.Close; set { if (_settingsService.Settings.Translate.Close != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.Close = value; this.RaisePropertyChanged(); } } }
    public string Translate_TranslateAll { get => _settingsService.Settings.Translate.TranslateAll; set { if (_settingsService.Settings.Translate.TranslateAll != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.TranslateAll = value; this.RaisePropertyChanged(); } } }
    public string Translate_ScanAll { get => _settingsService.Settings.Translate.ScanAll; set { if (_settingsService.Settings.Translate.ScanAll != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ScanAll = value; this.RaisePropertyChanged(); } } }
    public string Translate_ClearAll { get => _settingsService.Settings.Translate.ClearAll; set { if (_settingsService.Settings.Translate.ClearAll != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ClearAll = value; this.RaisePropertyChanged(); } } }
    public string Translate_ToggleSelect { get => _settingsService.Settings.Translate.ToggleSelect; set { if (_settingsService.Settings.Translate.ToggleSelect != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ToggleSelect = value; this.RaisePropertyChanged(); } } }
    public string Translate_AutoDetect { get => _settingsService.Settings.Translate.AutoDetect; set { if (_settingsService.Settings.Translate.AutoDetect != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.AutoDetect = value; this.RaisePropertyChanged(); } } }
    public string Translate_SelectionHoldModifier { get => _settingsService.Settings.Translate.SelectionHoldModifier; set { if (_settingsService.Settings.Translate.SelectionHoldModifier != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.SelectionHoldModifier = value; this.RaisePropertyChanged(); } } }
    public string Translate_SwitchToSnip { get => _settingsService.Settings.Translate.SwitchToSnip; set { if (_settingsService.Settings.Translate.SwitchToSnip != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.SwitchToSnip = value; this.RaisePropertyChanged(); } } }
    public string Translate_SwitchToRecord { get => _settingsService.Settings.Translate.SwitchToRecord; set { if (_settingsService.Settings.Translate.SwitchToRecord != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.SwitchToRecord = value; this.RaisePropertyChanged(); } } }
    public string Translate_ModeCursor { get => _settingsService.Settings.Translate.ModeCursor; set { if (_settingsService.Settings.Translate.ModeCursor != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ModeCursor = value; this.RaisePropertyChanged(); } } }
    public string Translate_ModeSingle { get => _settingsService.Settings.Translate.ModeSingle; set { if (_settingsService.Settings.Translate.ModeSingle != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ModeSingle = value; this.RaisePropertyChanged(); } } }
    public string Translate_ModeMulti { get => _settingsService.Settings.Translate.ModeMulti; set { if (_settingsService.Settings.Translate.ModeMulti != value) { this.RaisePropertyChanging(); _settingsService.Settings.Translate.ModeMulti = value; this.RaisePropertyChanged(); } } }

    public System.Collections.Generic.List<string> ModifierOptions { get; } = new() { "Shift", "Ctrl", "Alt", "None" };

    // Action Hotkeys

    public RecordingSettingsViewModel RecordingSettings { get; } = new();

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
        static string? MatchCrossActionConflict(
            string targetTag,
            string hotkey,
            params (string Tag, string CurrentHotkey, string ConflictKey)[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (targetTag != candidate.Tag && string.Equals(candidate.CurrentHotkey, hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.ConflictKey;
                }
            }

            return null;
        }

        // 1. Global Group (Idle state triggers)
        var globalGroup = new[] { "SnipHotkey", "RecordHotkey", "TranslateHotkey", "PinHotkey", "CopyHotkey" };
        
        // 2. Snip Group (Local to Screenshot mode)
        var snipGroup = new[] { 
            "Snip_Rectangle", "Snip_Ellipse", "Snip_Arrow", "Snip_Line", "Snip_Pen", 
            "Snip_Text", "Snip_Mosaic", "Snip_Blur", "Snip_Undo", "Snip_Redo", 
            "Snip_Clear", "Snip_Save", "Snip_Copy", "Snip_Close", 
            "Snip_Toolbar", "Snip_SelectionMode", "Snip_CropMode", "Snip_Pin", "Snip_FullscreenSelect",
            "Snip_SwitchToTranslate", "Snip_SwitchToRecord"
        };

        // 3. Record Group (Local to Video Recording mode)
        var recordGroup = new[] { 
            "Record_Rectangle", "Record_Ellipse", "Record_Arrow", "Record_Line", "Record_Pen", 
            "Record_Text", "Record_Mosaic", "Record_Blur", "Record_Undo", "Record_Redo", 
            "Record_Clear", "Record_Save", "Record_Copy", "Record_Close", "Record_Toolbar", 
            "Record_Action", "Record_Playback", "Record_FullscreenSelect",
            "Record_SwitchToSnip", "Record_SwitchToTranslate"
        };

        // 4. Translate Group (Local to Translation mode)
        var translateGroup = new[] { 
            "Translate_Action", "Translate_Pin", "Translate_Toolbar", "Translate_Close",
            "Translate_TranslateAll", "Translate_ScanAll", "Translate_ClearAll",
            "Translate_ToggleSelect", "Translate_AutoDetect",
            "Translate_SelectionHoldModifier",
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
            if (targetTag != "Snip_Pin" && !IsUnifiedPinHotkeyTag(targetTag) && Snip_Pin == hotkey) return "TipPin";
            if (targetTag != "Snip_FullscreenSelect" && Snip_FullscreenSelect == hotkey) return "ActionSelectFullscreen";
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
            if (targetTag != "Record_Action" && !IsUnifiedPinHotkeyTag(targetTag) && Record_Action == hotkey) return "ActionStartPin";
            if (targetTag != "Record_Playback" && Record_Playback == hotkey) return "ActionPlayback";
            if (targetTag != "Record_FullscreenSelect" && Record_FullscreenSelect == hotkey) return "ActionSelectFullscreen";
            if (targetTag != "Record_SwitchToSnip" && Record_SwitchToSnip == hotkey) return "SwitchToSnip";
            if (targetTag != "Record_SwitchToTranslate" && Record_SwitchToTranslate == hotkey) return "SwitchToTranslate";
        }
        else if (translateGroup.Contains(targetTag))
        {
            if (targetTag != "Translate_Action" && Translate_Action == hotkey) return "ActionHideTranslate";
            if (targetTag != "Translate_Pin" && !IsUnifiedPinHotkeyTag(targetTag) && Translate_Pin == hotkey) return "MenuPinTranslation";
            if (targetTag != "Translate_Toolbar" && Translate_Toolbar == hotkey) return "ActionToolbar";
            if (targetTag != "Translate_Close" && Translate_Close == hotkey) return "ActionClose";
            if (targetTag != "Translate_TranslateAll" && Translate_TranslateAll == hotkey) return "ActionTranslateAll";
            if (targetTag != "Translate_ScanAll" && Translate_ScanAll == hotkey) return "ActionScanAll";
            if (targetTag != "Translate_ClearAll" && Translate_ClearAll == hotkey) return "ActionClearAll";
            if (targetTag != "Translate_ToggleSelect" && Translate_ToggleSelect == hotkey) return "ActionToggleSelect";
            if (targetTag != "Translate_AutoDetect" && Translate_AutoDetect == hotkey) return "ActionAutoDetect";
            if (targetTag != "Translate_SelectionHoldModifier" && Translate_SelectionHoldModifier == hotkey) return "ActionSelectionHoldModifier";
            if (targetTag != "Translate_ModeCursor" && Translate_ModeCursor == hotkey) return "TranslateModeCursor";
            if (targetTag != "Translate_ModeSingle" && Translate_ModeSingle == hotkey) return "TranslateModeSingle";
            if (targetTag != "Translate_ModeMulti" && Translate_ModeMulti == hotkey) return "TranslateModeMulti";
            if (targetTag != "Translate_SwitchToSnip" && Translate_SwitchToSnip == hotkey) return "SwitchToSnip";
            if (targetTag != "Translate_SwitchToRecord" && Translate_SwitchToRecord == hotkey) return "SwitchToRecord";
        }

        var crossActionCandidates = new System.Collections.Generic.List<(string Tag, string CurrentHotkey, string ConflictKey)>
        {
            ("Translate_Action", Translate_Action, "ActionHideTranslate")
        };

        if (!IsUnifiedPinHotkeyTag(targetTag))
        {
            crossActionCandidates.Add(("Snip_Pin", Snip_Pin, "TipPin"));
            crossActionCandidates.Add(("Record_Action", Record_Action, "ActionStartPin"));
            crossActionCandidates.Add(("Translate_Pin", Translate_Pin, "MenuPinTranslation"));
        }

        var crossActionConflict = MatchCrossActionConflict(
            targetTag,
            hotkey,
            crossActionCandidates.ToArray());

        if (crossActionConflict != null)
        {
            return crossActionConflict;
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

    private string _llamaModelId = "qwen2.5-1.5b-instruct-q4";
    public string LlamaModelId
    {
        get => _llamaModelId;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            this.RaiseAndSetIfChanged(ref _llamaModelId, value);
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
            this.RaiseAndSetIfChanged(ref _llamaContextSize, Math.Clamp(value, 512, 8192));
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
            this.RaiseAndSetIfChanged(ref _llamaGpuLayers, Math.Max(0, value));
            if (!_isDataLoading)
            {
                _settingsService.Settings.LlamaGpuLayers = _llamaGpuLayers;
                MarkModifiedAndQueueSettingsSave();
            }
        }
    }

    public ObservableCollection<string> AvailableLlamaModels { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshLlamaModelsCommand { get; private set; } = null!;
    public bool HasDownloadedLlamaModels => AvailableLlamaModels.Count > 0 || AIResourceService.IsLlamaModelReady();
    public bool NoDownloadedLlamaModels => !HasDownloadedLlamaModels;

    public void RefreshLlamaModelCatalog()
    {
        var presets = AIResourceService.GetInstalledLlamaModelPresets();
        AvailableLlamaModels.Clear();
        foreach (var preset in presets)
        {
            AvailableLlamaModels.Add(preset.Id);
        }

        if (AvailableLlamaModels.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(LlamaModelId) || !AvailableLlamaModels.Contains(LlamaModelId))
            {
                LlamaModelId = AvailableLlamaModels[0];
            }
        }
        else
        {
            if (!AIResourceService.IsLlamaModelReady())
            {
                StatusText = LocalizationService.Instance["StatusLlamaModelNotReady"];
            }
        }

        this.RaisePropertyChanged(nameof(HasDownloadedLlamaModels));
        this.RaisePropertyChanged(nameof(NoDownloadedLlamaModels));
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

    private int _playbackUiFps = 30;
    public int PlaybackUiFps
    {
        get => _playbackUiFps;
        set => this.RaiseAndSetIfChanged(ref _playbackUiFps, Math.Clamp(value, 1, 120));
    }

    private int _playbackTimelineFps = 15;
    public int PlaybackTimelineFps
    {
        get => _playbackTimelineFps;
        set => this.RaiseAndSetIfChanged(ref _playbackTimelineFps, Math.Clamp(value, 1, 120));
    }

    public string[] AvailableRecordFormats { get; } = { "mp4", "mkv", "gif", "webm", "mov" };

    public async Task LoadSettingsAsync()
    {
        _isDataLoading = true;
        try
        {
            var snapshot = await _settingsPersistenceService.LoadAsync(_settingsService);
            ApplySettingsSnapshot(snapshot);
            RaiseSettingsBackedPropertyNotifications();
            IsModified = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            return false;
        }
    }
}
