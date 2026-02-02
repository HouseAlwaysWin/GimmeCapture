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
            _currentFontFamily = _fontEnglish;
            
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
                    ["Line"] = "Line",
                    ["Pen"] = "Pen",
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
                    ["RunOnStartup"] = "Run On Startup",
                    ["AutoCheckUpdates"] = "Auto Check Updates",
                    ["BorderThickness"] = "Border Thickness:",
                    ["MaskOpacity"] = "Mask Opacity:",
                    ["BorderColor"] = "Border Color:",
                    ["ThemeColor"] = "Theme Color:",
                    ["AutoSave"] = "Auto Save (Skip Dialog)",
                    ["SelectPath"] = "Select Path...",
                    ["CurrentPath"] = "Current Path:",
                    ["GlobalHotkeys"] = "Global Hotkeys",
                    ["SnipHotkey"] = "Snip Hotkey:",
                    ["CopyHotkey"] = "Snip & Copy:",
                    ["PinHotkey"] = "Snip & Pin:",
                    ["HotkeysActive"] = "* Global Listener Active (Kitsune Mode Ready)",
                    ["StartCapture"] = "Start Capture (Snip)",
                    ["SaveSettings"] = "Save",
                    ["ResetDefault"] = "Reset Defaults",
                    ["StatusReady"] = "Ready to Capture",
                    ["StatusSnip"] = "Snip Window Opened",
                    ["StatusSaved"] = "Settings Saved",
                        ["StatusReset"] = "Settings Reset to Default",
                        
                        // Updates
                        ["UpdateFound"] = "New version found: {0}. Download now?",
                        ["UpdateDownloading"] = "Downloading update... {0}%",
                        ["UpdateReady"] = "Update downloaded. Restart and install now?",
                        ["UpdateCheckTitle"] = "Update Check",
                        ["UpdateError"] = "Update failed: {0}",
                        ["CheckingUpdate"] = "Checking for updates...",
                        ["NoUpdateFound"] = "You are using the latest version.",

                    // Tooltips
                    ["TipCopy"] = "Copy (Ctrl+C)",
                    ["TipSave"] = "Save (Ctrl+S)",
                    ["TipPin"] = "Pin (F3)",
                    ["TipShapes"] = "Shapes",
                    ["TipLines"] = "Lines & Arrows",
                    ["Line"] = "Line",

                    // Context Menu
                    ["MenuCopyImage"] = "Copy Image",
                    ["MenuSaveImage"] = "Save Image As...",
                    ["MenuCopyVideo"] = "Copy Video",
                    ["MenuSaveVideo"] = "Save Video As...",
                    ["MenuShowToolbar"] = "Show Toolbar",
                    ["MenuWindowShadow"] = "Window Shadow",
                    ["MenuClose"] = "Close",

                    // Recording
                    ["TabRecord"] = "Record",
                    ["VideoFormat"] = "Output Format:",
                    ["VideoPath"] = "Output Folder:",
                    ["FixedPath"] = "Use Fixed Output Path",
                    ["UseFixedRecordPath"] = "Use Fixed Output Path",
                    ["RecordHotkey"] = "Record Hotkey:",
                    ["Record"] = "Record",
                    ["Pause"] = "Pause",
                    ["Stop"] = "Stop",
                    ["TipRecord"] = "Record (F2)",
                    ["TipSnipMode"] = "Switch to Snip Mode",
                    ["TipRecordMode"] = "Switch to Record Mode",
                    ["ShowPinDecoration"] = "Show Pin Decoration Corners",
            ["HidePinBorder"] = "Hide Pin Window Border",
            ["ShowSnipCursor"] = "Capture Mouse Cursor in Screenshot",
            ["ShowRecordCursor"] = "Capture Mouse Cursor in Recording",
            ["FFmpegStatusDownloading"] = "FFmpeg Downloading...",
            ["ComponentDownloadingProgress"] = "Downloading components... {0}%",
                },
                [Language.Chinese] = new Dictionary<string, string>
                {
                    ["Copy"] = "複製",
                    ["Save"] = "儲存",
                    ["Pin"] = "釘選",
                    ["Rect"] = "方框",
                    ["Arrow"] = "箭頭",
                    ["Line"] = "直線",
                    ["Pen"] = "畫筆",
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
                    ["RunOnStartup"] = "開機自動啟動",
                    ["AutoCheckUpdates"] = "自動檢查更新",
                    ["BorderThickness"] = "邊框粗細:",
                    ["MaskOpacity"] = "遮罩透明度:",
                    ["BorderColor"] = "邊框顏色:",
                    ["ThemeColor"] = "主題配色:",
                    ["AutoSave"] = "自動儲存 (跳過對話框)",
                    ["SelectPath"] = "選擇儲存路徑...",
                    ["CurrentPath"] = "目前路徑:",
                    ["GlobalHotkeys"] = "全域快捷鍵 (Global Hotkeys)",
                    ["SnipHotkey"] = "截圖熱鍵 (Snip):",
                    ["CopyHotkey"] = "截圖並複製 (Copy):",
                    ["PinHotkey"] = "截圖並釘選 (Pin):",
                    ["HotkeysActive"] = "* 已啟用全域監聽 (Kitsune Mode Ready)",
                    ["StartCapture"] = "啟動截圖",
                    ["SaveSettings"] = "保存",
                    ["ResetDefault"] = "重設預設",
                    ["StatusReady"] = "準備就緒",
                    ["StatusSnip"] = "擷圖視窗已開啟",
                    ["StatusSaved"] = "設定已保存",
                        ["StatusReset"] = "已重設設定",
                        
                        // Updates
                        ["UpdateFound"] = "發現新版本: {0}。立即下載？",
                        ["UpdateDownloading"] = "正在下載更新... {0}%",
                        ["UpdateReady"] = "更新已下載。立即重啟並安裝？",
                        ["UpdateCheckTitle"] = "檢查更新",
                        ["UpdateError"] = "更新失敗: {0}",
                        ["CheckingUpdate"] = "正在檢查更新...",
                        ["NoUpdateFound"] = "您已使用最新版本。",

                    // Tooltips
                    ["TipCopy"] = "複製 (Ctrl+C)",
                    ["TipSave"] = "儲存 (Ctrl+S)",
                    ["TipPin"] = "釘選 (F3)",
                    ["TipShapes"] = "形狀",
                    ["TipLines"] = "線條與箭頭",
                    ["Line"] = "直線",

                    // Context Menu
                    ["MenuCopyImage"] = "複製圖像",
                    ["MenuSaveImage"] = "圖像另存為...",
                    ["MenuCopyVideo"] = "複製影片",
                    ["MenuSaveVideo"] = "影片另存為...",
                    ["MenuShowToolbar"] = "顯示工具列",
                    ["MenuWindowShadow"] = "視窗陰影",
                    ["MenuClose"] = "關閉",

                    // Recording
                    ["TabRecord"] = "錄影",
                    ["VideoFormat"] = "輸出格式:",
                    ["VideoPath"] = "輸出資料夾:",
                    ["FixedPath"] = "使用固定輸出路徑",
                    ["UseFixedRecordPath"] = "使用固定輸出路徑",
                    ["RecordHotkey"] = "錄影熱鍵 (Record):",
                    ["Record"] = "錄影",
                    ["Pause"] = "暫停",
                    ["Stop"] = "停止",
                    ["TipRecord"] = "錄影 (F2)",
                    ["TipSnipMode"] = "切換至擷圖模式",
                    ["TipRecordMode"] = "切換至錄影模式",
                    ["ShowPinDecoration"] = "顯示釘選視窗角落裝飾",
            ["HidePinBorder"] = "隱藏釘選視窗邊框",
            ["ShowSnipCursor"] = "擷取畫面包含滑鼠游標",
            ["ShowRecordCursor"] = "錄影畫面包含滑鼠游標",
            ["FFmpegStatusDownloading"] = "正在下載 FFmpeg...",
            ["ComponentDownloadingProgress"] = "正在下載必要組件... {0}%",
                },
                [Language.Japanese] = new Dictionary<string, string>
                {
                    ["Copy"] = "コピー",
                    ["Save"] = "保存",
                    ["Pin"] = "ピン留め",
                    ["Rect"] = "短形",
                    ["Arrow"] = "矢印",
                    ["Line"] = "直線",
                    ["Pen"] = "ペン",
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
                    ["RunOnStartup"] = "スタートアップ起動",
                    ["AutoCheckUpdates"] = "自動更新チェック",
                    ["BorderThickness"] = "枠線の太さ:",
                    ["MaskOpacity"] = "マスクの不透明度:",
                    ["BorderColor"] = "枠線の色:",
                    ["ThemeColor"] = "テーマカラー:",
                    ["AutoSave"] = "自動保存 (ダイアログなし)",
                    ["SelectPath"] = "保存先を選択...",
                    ["CurrentPath"] = "現在のパス:",
                    ["GlobalHotkeys"] = "グローバルホットキー",
                    ["SnipHotkey"] = "キャプチャ (Snip):",
                    ["CopyHotkey"] = "コピーしてキャプチャ:",
                    ["PinHotkey"] = "ピン留めしてキャプチャ:",
                    ["HotkeysActive"] = "* グローバルリスナー有効 (Kitsune Mode Ready)",
                    ["StartCapture"] = "キャプチャ開始",
                    ["SaveSettings"] = "保存",
                    ["ResetDefault"] = "初期設定に戻す",
                    ["StatusReady"] = "キャプチャ準備完了",
                    ["StatusSnip"] = "キャプチャウィンドウが開きました",
                    ["StatusSaved"] = "設定を保存しました",
                        ["StatusReset"] = "初期設定に戻しました",
                        
                        // Updates
                        ["UpdateFound"] = "新しいバージョンが見つかりました: {0}。今すぐダウンロードしますか？",
                        ["UpdateDownloading"] = "更新プログラムをダウンロード中... {0}%",
                        ["UpdateReady"] = "更新プログラムがダウンロードされました。再起動してインストールしますか？",
                        ["UpdateCheckTitle"] = "アップデート確認",
                        ["UpdateError"] = "アップデートに失敗しました: {0}",
                        ["CheckingUpdate"] = "アップデートを確認中...",
                        ["NoUpdateFound"] = "最新バージョンを使用しています。",
                    
                    // Tooltips
                    ["TipCopy"] = "コピー (Ctrl+C)",
                    ["TipSave"] = "保存 (Ctrl+S)",
                    ["TipPin"] = "ピン留め (F3)",
                    ["TipShapes"] = "図形",
                    ["TipLines"] = "線と矢印",
                    ["Line"] = "直線",

                    // Context Menu
                    ["MenuCopyImage"] = "画像をコピー",
                    ["MenuSaveImage"] = "名前を付けて画像を保存...",
                    ["MenuCopyVideo"] = "動画をコピー",
                    ["MenuSaveVideo"] = "名前を付けて動画を保存...",
                    ["MenuShowToolbar"] = "ツールバーを表示",
                    ["MenuWindowShadow"] = "ウィンドウの影",
                    ["MenuClose"] = "閉じる",

                    // Recording
                    ["TabRecord"] = "録画",
                    ["VideoFormat"] = "出力形式:",
                    ["VideoPath"] = "出力フォルダー:",
                    ["FixedPath"] = "固定出力パスを使用する",
                    ["UseFixedRecordPath"] = "固定出力パスを使用する",
                    ["RecordHotkey"] = "録画ホットキー:",
                    ["Record"] = "録画",
                    ["Pause"] = "一時停止",
                    ["Stop"] = "停止",
                    ["TipRecord"] = "録画 (F2)",
                    ["TipSnipMode"] = "スニップモードに切り替える",
                    ["TipRecordMode"] = "録画モードに切り替える",
                    ["ShowPinDecoration"] = "ピン留めウィンドウの装飾を表示する",
            ["HidePinBorder"] = "ピン留めウィンドウの境界線を非表示にする",
            ["ShowSnipCursor"] = "スクリーンショットにマウスカーソルを含める",
            ["ShowRecordCursor"] = "録画にマウスカーソルを含める",
            ["FFmpegStatusDownloading"] = "FFmpeg ダウンロード中...",
            ["ComponentDownloadingProgress"] = "コンポーネントをダウンロード中... {0}%",
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
