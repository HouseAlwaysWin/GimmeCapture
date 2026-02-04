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
                    ["StatusReady"] = "Ready",
                    ["StatusSnip"] = "Snipping...",
                    ["StatusSaved"] = "Saved Successfully!",
                    ["StatusModified"] = "Settings have been modified. Click Save to apply.",
                    ["StatusReset"] = "Settings Reset to Default",
                    ["StatusCopied"] = "Copied to Clipboard!",
                    ["SaveSettings"] = "Save",
                    ["ResetDefault"] = "Reset Defaults",
                    ["UnsavedTitle"] = "Unsaved Changes",
                    ["UnsavedMessage"] = "You have unsaved changes. Would you like to save them before closing?",
                    ["SaveFailed"] = "Failed to save settings. Please check if the configuration file is writable.",
                    ["Yes"] = "Yes",
                    ["No"] = "No",
                    ["No"] = "No",
                    ["No"] = "No",
                    ["Cancel"] = "Cancel",
                    ["GitHubProject"] = "GitHub Project",
                        
                        // Updates
                        ["UpdateFound"] = "New version found: {0}. Download now?",
                        ["UpdateDownloading"] = "Downloading update... {0}%",
                        ["UpdateReady"] = "Update downloaded. Restart and install now?",
                        ["UpdateCheckTitle"] = "Update Check",
                        ["UpdateError"] = "Update failed: {0}",
                        ["CheckingUpdate"] = "Checking for updates...",
                        ["NoUpdateFound"] = "You are using the latest version.",
                        ["CheckForUpdates"] = "Check for Updates",
                        ["FFmpegNotReady"] = "FFmpeg components are still downloading. Please wait before recording.",
                        ["UpdateBtnConfirm"] = "ENTER THE FOX PIT",
                        ["UpdateBtnCancel"] = "NOT TODAY",
                        ["UpdateBtnOk"] = "FOX GOD APPROVES",

                        ["CaptureModeNormal"] = "Screenshot Mode",
                        ["CaptureModeRecord"] = "Recording Mode",

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
                    ["FixedPath"] = "Auto Save (Skip Dialog)",
                    ["UseFixedRecordPath"] = "Auto Save (Skip Dialog)",
                    ["RecordHotkey"] = "Record Hotkey:",
                    ["Record"] = "Record",
                    ["Pause"] = "Pause",
                    ["Stop"] = "Stop",
                    ["TipRecord"] = "Record (F2)",
                    ["TipSnipMode"] = "Switch to Snip Mode",
                    ["TipRecordMode"] = "Switch to Record Mode",
                    ["HideSnipPinDecoration"] = "Hide Image Pin Decoration Corners",
                    ["HideSnipPinBorder"] = "Hide Image Pin Window Border",
                    ["HideRecordPinDecoration"] = "Hide Video Pin Decoration Corners",
                    ["HideRecordPinBorder"] = "Hide Video Pin Window Border",
                    ["HideSnipSelectionDecoration"] = "Hide Snip Selection Decoration Corners",
                    ["HideSnipSelectionBorder"] = "Hide Snip Selection Border",
                    ["HideRecordSelectionDecoration"] = "Hide Record Selection Decoration Corners",
                    ["HideRecordSelectionBorder"] = "Hide Record Selection Border",
                    ["ShowSnipCursor"] = "Capture Mouse Cursor in Screenshot",
            ["ShowRecordCursor"] = "Capture Mouse Cursor in Recording",
            ["FFmpegStatusDownloading"] = "FFmpeg Downloading...",
            ["ComponentDownloadingProgress"] = "Downloading components... {0}%",
            ["TempPath"] = "Temp Folder:",
            ["WingScale"] = "Wing Scale:",
            ["IconScale"] = "Icon Scale:",
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
                    ["StartCapture"] = "開始擷取 (側寫)",
                    ["StatusReady"] = "就緒",
                    ["StatusSnip"] = "正在擷取...",
                    ["StatusSaved"] = "已成功儲存！",
                    ["StatusModified"] = "設定已變更，別忘了儲存喔！",
                    ["StatusReset"] = "設定已重設為預設值",
                    ["StatusCopied"] = "已複製到剪貼簿！",
                    ["SaveSettings"] = "保存",
                    ["ResetDefault"] = "重設預設",
                    ["UnsavedTitle"] = "尚未儲存變更",
                    ["UnsavedMessage"] = "您有些設定尚未儲存。是否要在關閉前保存這些變更？",
                    ["SaveFailed"] = "設定儲存失敗。請檢查設定檔是否被佔用或唯讀。",
                    ["Yes"] = "是",
                    ["No"] = "否",
                    ["No"] = "否",
                    ["No"] = "否",
                    ["Cancel"] = "取消",
                    ["GitHubProject"] = "GitHub 專案頁面",
                        
                        // Updates
                        ["UpdateFound"] = "發現新版本: {0}。立即下載？",
                        ["UpdateDownloading"] = "正在下載更新... {0}%",
                        ["UpdateReady"] = "更新已下載。立即重啟並安裝？",
                        ["UpdateCheckTitle"] = "檢查更新",
                        ["UpdateError"] = "更新失敗: {0}",
                        ["CheckingUpdate"] = "正在檢查更新...",
                        ["NoUpdateFound"] = "您已使用最新版本。",
                        ["CheckForUpdates"] = "檢查更新",
                        ["FFmpegNotReady"] = "必要組件 (FFmpeg) 正在下載中，請稍候再進行錄影。",
                        ["UpdateBtnConfirm"] = "進入狐穴 (Update)",
                        ["UpdateBtnCancel"] = "下次一定 (Not Today)",
                        ["UpdateBtnOk"] = "狐神准許 (OK)",

                        ["CaptureModeNormal"] = "截圖模式",
                        ["CaptureModeRecord"] = "錄影模式",

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
                    ["FixedPath"] = "自動儲存 (跳過對話框)",
                    ["UseFixedRecordPath"] = "自動儲存 (跳過對話框)",
                    ["RecordHotkey"] = "錄影熱鍵 (Record):",
                    ["Record"] = "錄影",
                    ["Pause"] = "暫停",
                    ["Stop"] = "停止",
                    ["TipRecord"] = "錄影 (F2)",
                    ["TipSnipMode"] = "切換至擷圖模式",
                    ["TipRecordMode"] = "切換至錄影模式",
                    ["HideSnipPinDecoration"] = "隱藏圖像釘選角落裝飾",
                    ["HideSnipPinBorder"] = "隱藏圖像釘選邊框",
                    ["HideRecordPinDecoration"] = "隱藏影片釘選角落裝飾",
                    ["HideRecordPinBorder"] = "隱藏影片釘選邊框",
                    ["HideSnipSelectionDecoration"] = "隱藏截圖角落裝飾",
                    ["HideSnipSelectionBorder"] = "隱藏截圖邊框",
                    ["HideRecordSelectionDecoration"] = "隱藏錄影角落裝飾",
                    ["HideRecordSelectionBorder"] = "隱藏錄影邊框",
                    ["ShowSnipCursor"] = "在截圖中擷取滑鼠指標",
            ["ShowRecordCursor"] = "錄影畫面包含滑鼠游標",
            ["FFmpegStatusDownloading"] = "正在下載 FFmpeg...",
            ["ComponentDownloadingProgress"] = "正在下載必要組件... {0}%",
            ["TempPath"] = "暫存資料夾:",
            ["WingScale"] = "翅膀大小:",
            ["IconScale"] = "裝飾大小:",
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
                    ["StatusReady"] = "待機中",
                    ["StatusSnip"] = "キャプチャ中...",
                    ["StatusSaved"] = "保存しました！",
                    ["StatusModified"] = "設定が変更されました。保存をお忘れなく！",
                    ["StatusReset"] = "設定を初期化しました",
                    ["StatusCopied"] = "クリップボードにコピーしました！",
                    ["SaveSettings"] = "保存",
                    ["ResetDefault"] = "初期設定に戻す",
                    ["UnsavedTitle"] = "未保存の変更",
                    ["UnsavedMessage"] = "保存されていない変更があります。閉じる前に保存しますか？",
                    ["SaveFailed"] = "設定の保存に失敗しました。設定ファイルが書き込み可能か確認してください。",
                    ["Yes"] = "はい",
                    ["No"] = "いいえ",
                    ["No"] = "いいえ",
                    ["No"] = "いいえ",
                    ["Cancel"] = "キャンセル",
                    ["GitHubProject"] = "GitHub プロジェクト",
                        
                        // Updates
                        ["UpdateFound"] = "新しいバージョンが見つかりました: {0}。今すぐダウンロードしますか？",
                        ["UpdateDownloading"] = "更新プログラムをダウンロード中... {0}%",
                        ["UpdateReady"] = "更新プログラムがダウンロードされました。再起動してインストールしますか？",
                        ["UpdateCheckTitle"] = "アップデート確認",
                        ["UpdateError"] = "アップデートに失敗しました: {0}",
                        ["CheckingUpdate"] = "アップデートを確認中...",
                        ["NoUpdateFound"] = "最新バージョンを使用しています。",
                        ["CheckForUpdates"] = "アップデートを確認",
                        ["FFmpegNotReady"] = "録画に必要なコンポーネント (FFmpeg) をダウンロード中です。完了までお待ちください。",
                        ["UpdateBtnConfirm"] = "キツネの穴へ (Update)",
                        ["UpdateBtnCancel"] = "今はしない (Not Today)",
                        ["UpdateBtnOk"] = "キツネ様のお導き (OK)",

                        ["CaptureModeNormal"] = "スニップモード",
                        ["CaptureModeRecord"] = "録画モード",
                    
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
                    ["FixedPath"] = "自動保存 (ダイアログなし)",
                    ["UseFixedRecordPath"] = "自動保存 (ダイアログなし)",
                    ["RecordHotkey"] = "録画ホットキー:",
                    ["Record"] = "録画",
                    ["Pause"] = "一時停止",
                    ["Stop"] = "停止",
                    ["TipRecord"] = "録画 (F2)",
                    ["TipSnipMode"] = "スニップモードに切り替える",
                    ["TipRecordMode"] = "録画モードに切り替える",
                    ["HideSnipPinDecoration"] = "画像ピン留め装飾を非表示",
                    ["HideSnipPinBorder"] = "画像ピン留め境界線を非表示",
                    ["HideRecordPinDecoration"] = "ビデオピン留め装飾を非表示",
                    ["HideRecordPinBorder"] = "ビデオピン留め境界線を非表示",
                    ["HideSnipSelectionDecoration"] = "キャプチャ装飾を非表示",
                    ["HideSnipSelectionBorder"] = "キャプチャ境界線を非表示",
                    ["HideRecordSelectionDecoration"] = "録画装飾を非表示",
                    ["HideRecordSelectionBorder"] = "録画境界線を非表示",
                    ["ShowSnipCursor"] = "スクリーンショットにマウスカーソルを含める",
            ["ShowRecordCursor"] = "録画にマウスカーソルを含める",
            ["FFmpegStatusDownloading"] = "FFmpeg ダウンロード中...",
            ["ComponentDownloadingProgress"] = "コンポーネントをダウンロード中... {0}%",
            ["TempPath"] = "一時フォルダー:",
            ["WingScale"] = "翼のサイズ:",
            ["IconScale"] = "アイコンのサイズ:",
                },
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
