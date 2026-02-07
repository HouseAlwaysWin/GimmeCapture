using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using ReactiveUI;

namespace GimmeCapture.Services.Core;
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
                    ["TabRecord"] = "Record",
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
                    ["RemoveBackground"] = "Remove Background",
                    ["AIDownloadTitle"] = "AI Modules Required",
                    ["AIDownloadPrompt"] = "AI Background Removal requires additional modules (~300MB). These will be downloaded and saved to your AppData folder.\n\nDownload now?",
                    ["DownloadingAI"] = "Downloading AI Modules...",
                    ["ProcessingAI"] = "AI Processing...",
                    ["StatusSnip"] = "Snipping...",
                    ["StatusSaved"] = "Saved Successfully!",
                    ["StatusModified"] = "Settings have been modified. Click Save to apply.",
                    ["StatusReset"] = "Settings Reset to Default",
                    ["StatusCopied"] = "Copied to Clipboard!",
                    ["StatusProcessing"] = "Processing...",
                    ["StatusSaving"] = "Saving...",
                    ["SaveSettings"] = "Save",
                    ["ResetDefault"] = "Reset Defaults",
                    ["UnsavedTitle"] = "Unsaved Changes",
                    ["UnsavedMessage"] = "You have unsaved changes. Would you like to save them before closing?",
                    ["SaveFailed"] = "Failed to save settings. Please check if the configuration file is writable.",
                    ["Yes"] = "Yes",
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
                    ["FFmpegDownloadConfirm"] = "FFmpeg is required for recording. Download and install now?",
                    ["AIDownloadConfirm"] = "Interactive AI Selection requires additional modules (approx. 100MB). Download now?",
                    ["TabModules"] = "Modules",
                    ["ModuleFFmpegDescription"] = "Multimedia framework for video recording and playback.",
                    ["ModuleAIDescription"] = "AI models for background removal and object selection (U2Net/SAM2).",
                    ["Installed"] = "Installed",
                    ["NotInstalled"] = "Not Installed",
                    ["Install"] = "Install",
                    ["Remove"] = "Remove",
                    ["Pending"] = "Pending...",
                    ["ComponentDownloadingProgress"] = "Downloading Component...",
                    ["UpdateBtnConfirm"] = "Update Now",
                    ["UpdateBtnCancel"] = "Later",
                    ["UpdateBtnOk"] = "OK",
                    ["CancelDownload"] = "Cancel Download",
                    ["ConfirmCancelDownload"] = "Are you sure you want to cancel the download?",
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
                    ["TabRecord"] = "錄影",
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
                    ["RemoveBackground"] = "AI 去背",
                    ["AIDownloadTitle"] = "需要下載 AI 模組",
                    ["AIDownloadPrompt"] = "AI 去背功能需要額外的組件 (~300MB)。這些檔案將會下載並儲存至您的 AppData 資料夾。\n\n現在下載嗎？",
                    ["DownloadingAI"] = "正在下載 AI 模組...",
                    ["ProcessingAI"] = "AI 運算中...",
                    ["StatusSnip"] = "正在擷取...",
                    ["StatusSaved"] = "已成功儲存！",
                    ["StatusModified"] = "設定已變更，別忘了儲存喔！",
                    ["StatusReset"] = "設定已重設為預設值",
                    ["StatusCopied"] = "已複製到剪貼簿！",
                    ["StatusProcessing"] = "正在處理...",
                    ["StatusSaving"] = "正在儲存...",
                    ["SaveSettings"] = "保存",
                    ["ResetDefault"] = "重設預設",
                    ["UnsavedTitle"] = "尚未儲存變更",
                    ["UnsavedMessage"] = "您有些設定尚未儲存。是否要在關閉前保存這些變更？",
                    ["SaveFailed"] = "設定儲存失敗。請檢查設定檔是否被佔用或唯讀。",
                    ["Yes"] = "是",
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
                    ["FFmpegDownloadConfirm"] = "錄影功能需要必要組件 (FFmpeg)。現在要下載並安裝嗎？",
                    ["AIDownloadConfirm"] = "互動選取功能需要下載 AI 模組 (約 100MB)。現在要下載嗎？",
                    ["TabModules"] = "模組管理",
                    ["ModuleFFmpegDescription"] = "用於影片錄製與播放的多媒體架構 (FFmpeg)。",
                    ["ModuleAIDescription"] = "用於去背與物體選取的 AI 模型 (U2Net/SAM2)。",
                    ["Installed"] = "已安裝",
                    ["NotInstalled"] = "未安裝",
                    ["Install"] = "安裝",
                    ["Remove"] = "移除",
                    ["Pending"] = "等待中...",
                    ["ComponentDownloadingProgress"] = "組件下載中",
                    ["UpdateBtnConfirm"] = "立即更新",
                    ["UpdateBtnCancel"] = "稍後",
                    ["UpdateBtnOk"] = "確定",
                    ["CancelDownload"] = "取消下載",
                    ["ConfirmCancelDownload"] = "確定要取消下載嗎？",
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
                    ["TabRecord"] = "録画",
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
                    ["StatusProcessing"] = "処理中...",
                    ["StatusSaving"] = "保存中...",
                    ["SaveSettings"] = "保存",
                    ["ResetDefault"] = "初期設定に戻す",
                    ["UnsavedTitle"] = "未保存の変更",
                    ["UnsavedMessage"] = "保存されていない変更があります。閉じる前に保存しますか？",
                    ["SaveFailed"] = "設定の保存に失敗しました。設定ファイルが書き込み可能か確認してください。",
                    ["Yes"] = "はい",
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
                    ["FFmpegNotReady"] = "錄画に必要なコンポーネント (FFmpeg) をダウンロード中です。完了までお待ちください。",
                    ["FFmpegDownloadConfirm"] = "録画機能には必要コンポーネント (FFmpeg) が必要です。今すぐ下載してインストールしますか？",
                    ["AIDownloadConfirm"] = "インタラクティブ選択機能には AI モジュール (約 100MB) のダウンロードが必要です。今すぐダウンロードしますか？",
                    ["TabModules"] = "モジュール管理",
                    ["ModuleFFmpegDescription"] = "動画の録画と再生に必要なマルチメディアフレームワーク (FFmpeg)。",
                    ["ModuleAIDescription"] = "背景削除とオブジェクト選択用の AI モデル (U2Net/SAM2)。",
                    ["Installed"] = "インストール済み",
                    ["NotInstalled"] = "未インストール",
                    ["Install"] = "インストール",
                    ["Remove"] = "削除",
                    ["Pending"] = "待機中...",
                    ["ComponentDownloadingProgress"] = "コンポーネントをダウンロード中",
                    ["UpdateBtnConfirm"] = "今すぐ更新",
                    ["UpdateBtnCancel"] = "後で",
                    ["UpdateBtnOk"] = "OK",
                    ["CancelDownload"] = "ダウンロードをキャンセル",
                    ["ConfirmCancelDownload"] = "ダウンロードをキャンセルしてもよろしいですか？",
                    ["UpdateCheckTitle"] = "更新の確認",
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
