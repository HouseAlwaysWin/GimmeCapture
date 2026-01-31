using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using ReactiveUI;

namespace GimmeCapture.Services
{
    public enum Language
    {
        English,
        Chinese, // Traditional
        Japanese
    }

    public class LocalizationService : ReactiveObject
    {
        private static LocalizationService? _instance;
        public static LocalizationService Instance => _instance ??= new LocalizationService();

        private Language _currentLanguage;
        public Language CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    this.RaiseAndSetIfChanged(ref _currentLanguage, value);
                    UpdateFont(value);
                    
                    // Notify indexer changes for binding updates
                    this.RaisePropertyChanged("Item");
                    this.RaisePropertyChanged("Item[]"); 
                }
            }
        }

        private FontFamily _currentFontFamily;
        public FontFamily CurrentFontFamily
        {
            get => _currentFontFamily;
            private set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
        }

        // Font Paths
        private readonly FontFamily _fontEnglish = new FontFamily("avares://GimmeCapture/Assets/Fonts/Cinzel/Cinzel-VariableFont_wght.ttf#Cinzel");
        private readonly FontFamily _fontChinese = new FontFamily("avares://GimmeCapture/Assets/Fonts/Noto_Serif_TC/NotoSerifTC-VariableFont_wght.ttf#Noto Serif TC");
        private readonly FontFamily _fontJapanese = new FontFamily("avares://GimmeCapture/Assets/Fonts/Noto_Serif_JP/NotoSerifJP-VariableFont_wght.ttf#Noto Serif JP");

        private readonly Dictionary<Language, Dictionary<string, string>> _translations;

        private LocalizationService()
        {
            // Initialize Translations
            _translations = new Dictionary<Language, Dictionary<string, string>>
            {
                [Language.English] = new Dictionary<string, string>
                {
                    ["Copy"] = "Copy",
                    ["Save"] = "Save",
                    ["Pin"] = "Pin",
                    ["Rect"] = "Rectangle",
                    ["Arrow"] = "Arrow",
                    ["Text"] = "Text",
                    ["Color"] = "Color",
                    ["Size"] = "Size",
                    ["Font"] = "Font",
                    ["Custom"] = "Custom",
                    ["HexColor"] = "Hex Color",
                    ["Undo"] = "Undo",
                    ["Clear"] = "Clear",
                    ["Language"] = "Language",
                    
                    // MainWindow
                    ["TabGeneral"] = "General",
                    ["TabSnip"] = "Snip",
                    ["TabOutput"] = "Output",
                    ["TabControl"] = "Control",
                    ["TabAbout"] = "About",
                    ["RunOnStartup"] = "Run On Startup (Coming Soon)",
                    ["AutoCheckUpdates"] = "Auto Check Updates",
                    ["BorderThickness"] = "Border Thickness:",
                    ["MaskOpacity"] = "Mask Opacity:",
                    ["BorderColor"] = "Border Color (Default Red):",
                    ["AutoSave"] = "Auto Save (Skip Dialog)",
                    ["SelectPath"] = "Select Path...",
                    ["CurrentPath"] = "Current Path:",
                    ["GlobalHotkeys"] = "Global Hotkeys",
                    ["SnipHotkey"] = "Snip Hotkey:",
                    ["CopyHotkey"] = "Snip & Copy:",
                    ["PinHotkey"] = "Snip & Pin:",
                    ["HotkeysActive"] = "* Global Listener Active (Kitsune Mode Ready)",
                    ["StartCapture"] = "Start Capture (Snip)",
                    ["Confirm"] = "OK (Save)",
                    ["Cancel"] = "Cancel"
                },
                [Language.Chinese] = new Dictionary<string, string>
                {
                    ["Copy"] = "複製",
                    ["Save"] = "儲存",
                    ["Pin"] = "釘選",
                    ["Rect"] = "方框",
                    ["Arrow"] = "箭頭",
                    ["Text"] = "文字",
                    ["Color"] = "顏色",
                    ["Size"] = "大小",
                    ["Font"] = "字型",
                    ["Custom"] = "自訂",
                    ["HexColor"] = "色碼",
                    ["Undo"] = "復原",
                    ["Clear"] = "清除",
                    ["Language"] = "語言",

                    // MainWindow
                    ["TabGeneral"] = "一般",
                    ["TabSnip"] = "擷圖",
                    ["TabOutput"] = "輸出",
                    ["TabControl"] = "控制",
                    ["TabAbout"] = "關於",
                    ["RunOnStartup"] = "開機自動啟動 (尚未實作)",
                    ["AutoCheckUpdates"] = "自動檢查更新",
                    ["BorderThickness"] = "邊框粗細:",
                    ["MaskOpacity"] = "遮罩透明度:",
                    ["BorderColor"] = "邊框顏色 (預設紅色):",
                    ["AutoSave"] = "自動儲存 (跳過對話框)",
                    ["SelectPath"] = "選擇儲存路徑...",
                    ["CurrentPath"] = "目前路徑:",
                    ["GlobalHotkeys"] = "全域快捷鍵 (Global Hotkeys)",
                    ["SnipHotkey"] = "截圖熱鍵 (Snip):",
                    ["CopyHotkey"] = "截圖並複製 (Copy):",
                    ["PinHotkey"] = "截圖並釘選 (Pin):",
                    ["HotkeysActive"] = "* 已啟用全域監聽 (Kitsune Mode Ready)",
                    ["StartCapture"] = "啟動截圖 (Snip)",
                    ["Confirm"] = "確定 (Save)",
                    ["Cancel"] = "取消"
                },
                [Language.Japanese] = new Dictionary<string, string>
                {
                    ["Copy"] = "コピー",
                    ["Save"] = "保存",
                    ["Pin"] = "ピン留め",
                    ["Rect"] = "短形",
                    ["Arrow"] = "矢印",
                    ["Text"] = "テキスト",
                    ["Color"] = "色",
                    ["Size"] = "サイズ",
                    ["Font"] = "フォント",
                    ["Custom"] = "カスタム",
                    ["HexColor"] = "カラーコード",
                    ["Undo"] = "元に戻す",
                    ["Clear"] = "クリア",
                    ["Language"] = "言語",

                    // MainWindow
                    ["TabGeneral"] = "一般",
                    ["TabSnip"] = "キャプチャ",
                    ["TabOutput"] = "出力",
                    ["TabControl"] = "操作",
                    ["TabAbout"] = "バージョン",
                    ["RunOnStartup"] = "スタートアップ起動 (未実装)",
                    ["AutoCheckUpdates"] = "自動更新チェック",
                    ["BorderThickness"] = "枠線の太さ:",
                    ["MaskOpacity"] = "マスクの不透明度:",
                    ["BorderColor"] = "枠線の色 (デフォルト赤):",
                    ["AutoSave"] = "自動保存 (ダイアログなし)",
                    ["SelectPath"] = "保存先を選択...",
                    ["CurrentPath"] = "現在のパス:",
                    ["GlobalHotkeys"] = "グローバルホットキー",
                    ["SnipHotkey"] = "キャプチャ (Snip):",
                    ["CopyHotkey"] = "コピーしてキャプチャ:",
                    ["PinHotkey"] = "ピン留めしてキャプチャ:",
                    ["HotkeysActive"] = "* グローバルリスナー有効 (Kitsune Mode Ready)",
                    ["StartCapture"] = "キャプチャ開始",
                    ["Confirm"] = "OK (保存)",
                    ["Cancel"] = "キャンセル"
                }
            };

            // Set Default
            // CurrentLanguage = Language.English; // Trigger setter
            UpdateFont(Language.English); 
        }

        private void UpdateFont(Language lang)
        {
             switch (lang)
             {
                 case Language.English:
                     CurrentFontFamily = _fontEnglish;
                     break;
                 case Language.Chinese:
                     CurrentFontFamily = _fontChinese;
                     break;
                 case Language.Japanese:
                     CurrentFontFamily = _fontJapanese;
                     break;
             }
        }

        public string this[string key]
        {
            get
            {
                if (_translations.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var val))
                {
                    return val;
                }
                return key; // Fallback
            }
        }

        public Dictionary<string, string> CurrentTranslations => _translations[_currentLanguage];

        public void CycleLanguage()
        {
            var next = (int)CurrentLanguage + 1;
            if (next > (int)Language.Japanese) next = 0;
            CurrentLanguage = (Language)next;
        }
    }
}
