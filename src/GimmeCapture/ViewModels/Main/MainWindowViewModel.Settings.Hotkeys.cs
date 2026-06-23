using System;
using System.Collections.Generic;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

// Per-mode hotkey properties (Snip/Record/Translate), the unified-pin helpers, and hotkey
// conflict checking. Split out of MainWindowViewModel.Settings.cs (god class reduction) — no behavior change.
public partial class MainWindowViewModel
{
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

    // Thin wrapper: snapshot the current hotkey values by tag and delegate the (pure) conflict
    // logic to HotkeyConflictValidator so it can be unit-tested in isolation. Tag names match
    // property names exactly.
    public string? CheckHotkeyConflict(string targetTag, string hotkey)
    {
        var current = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SnipHotkey"] = SnipHotkey,
            ["RecordHotkey"] = RecordHotkey,
            ["TranslateHotkey"] = TranslateHotkey,
            ["TextCopyHotkey"] = TextCopyHotkey,

            ["Snip_Rectangle"] = Snip_Rectangle,
            ["Snip_Ellipse"] = Snip_Ellipse,
            ["Snip_Arrow"] = Snip_Arrow,
            ["Snip_Line"] = Snip_Line,
            ["Snip_Pen"] = Snip_Pen,
            ["Snip_Text"] = Snip_Text,
            ["Snip_Mosaic"] = Snip_Mosaic,
            ["Snip_Blur"] = Snip_Blur,
            ["Snip_Undo"] = Snip_Undo,
            ["Snip_Redo"] = Snip_Redo,
            ["Snip_Clear"] = Snip_Clear,
            ["Snip_Save"] = Snip_Save,
            ["Snip_Copy"] = Snip_Copy,
            ["Snip_Close"] = Snip_Close,
            ["Snip_Toolbar"] = Snip_Toolbar,
            ["Snip_SelectionMode"] = Snip_SelectionMode,
            ["Snip_CropMode"] = Snip_CropMode,
            ["Snip_RemoveBackground"] = Snip_RemoveBackground,
            ["Snip_MagicWand"] = Snip_MagicWand,
            ["Snip_Pin"] = Snip_Pin,
            ["Snip_FullscreenSelect"] = Snip_FullscreenSelect,
            ["Snip_SwitchToTranslate"] = Snip_SwitchToTranslate,
            ["Snip_SwitchToRecord"] = Snip_SwitchToRecord,

            ["Record_Rectangle"] = Record_Rectangle,
            ["Record_Ellipse"] = Record_Ellipse,
            ["Record_Arrow"] = Record_Arrow,
            ["Record_Line"] = Record_Line,
            ["Record_Pen"] = Record_Pen,
            ["Record_Text"] = Record_Text,
            ["Record_Mosaic"] = Record_Mosaic,
            ["Record_Blur"] = Record_Blur,
            ["Record_Undo"] = Record_Undo,
            ["Record_Redo"] = Record_Redo,
            ["Record_Clear"] = Record_Clear,
            ["Record_Save"] = Record_Save,
            ["Record_Copy"] = Record_Copy,
            ["Record_Close"] = Record_Close,
            ["Record_Toolbar"] = Record_Toolbar,
            ["Record_Action"] = Record_Action,
            ["Record_Playback"] = Record_Playback,
            ["Record_FullscreenSelect"] = Record_FullscreenSelect,
            ["Record_SwitchToSnip"] = Record_SwitchToSnip,
            ["Record_SwitchToTranslate"] = Record_SwitchToTranslate,

            ["Translate_Action"] = Translate_Action,
            ["Translate_Pin"] = Translate_Pin,
            ["Translate_Toolbar"] = Translate_Toolbar,
            ["Translate_Close"] = Translate_Close,
            ["Translate_TranslateAll"] = Translate_TranslateAll,
            ["Translate_ScanAll"] = Translate_ScanAll,
            ["Translate_ClearAll"] = Translate_ClearAll,
            ["Translate_ToggleSelect"] = Translate_ToggleSelect,
            ["Translate_AutoDetect"] = Translate_AutoDetect,
            ["Translate_SelectionHoldModifier"] = Translate_SelectionHoldModifier,
            ["Translate_ModeCursor"] = Translate_ModeCursor,
            ["Translate_ModeSingle"] = Translate_ModeSingle,
            ["Translate_ModeMulti"] = Translate_ModeMulti,
            ["Translate_SwitchToSnip"] = Translate_SwitchToSnip,
            ["Translate_SwitchToRecord"] = Translate_SwitchToRecord,
        };

        return HotkeyConflictValidator.FindConflict(targetTag, hotkey, current);
    }
}
