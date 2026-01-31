# GimmeCapture!! - 實作計畫書

> **The Metal Image/Video Snip Tool** - Inspired by BABYMETAL 🦊

## 專案概述

建立一個跨平台截圖/錄影軟體，使用 AvaloniaUI 框架，具備 BABYMETAL 視覺風格。

---

## 第一階段：基礎設施建置 (The One - Foundations)

### [NEW] [IScreenCaptureService.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Services/IScreenCaptureService.cs)

截圖服務介面：
```csharp
public interface IScreenCaptureService
{
    Task<SKBitmap> CaptureScreenAsync(Rect region);
    Task CopyToClipboardAsync(SKBitmap bitmap);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
}
```

### [NEW] [ScreenCaptureService.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Services/ScreenCaptureService.cs)

實作截圖邏輯。
*   **Windows**: 使用 `System.Drawing.Common` 的 `Graphics.CopyFromScreen` 抓取螢幕，再轉為 `SKBitmap`。
*   **Linux/Mac**: 未來擴充 (可能需要 `X11` 或 `SCKit` 相關庫)。

為了簡化 Phase 1，我們先實作 Windows 版本。需要安裝 `System.Drawing.Common` NuGet 套件。
│   └── implementation_plan.md    # 實作計畫書
├── src/
│   └── GimmeCapture/
│       ├── GimmeCapture.csproj
│       ├── Program.cs
│       ├── App.axaml
│       ├── App.axaml.cs
│       ├── Styles/
│       │   └── BabymetalTheme.axaml      # 全局樣式資源字典
│       ├── Models/
│       │   └── CaptureRegion.cs          # 截圖區域資料模型
│       ├── ViewModels/
│       │   ├── ViewModelBase.cs          # ViewModel 基底類別
│       │   ├── MainWindowViewModel.cs    # 主視窗 ViewModel
│       │   └── SnipWindowViewModel.cs    # 截圖視窗 ViewModel
│       ├── Views/
│       │   ├── MainWindow.axaml
│       │   ├── MainWindow.axaml.cs
│       │   ├── SnipWindow.axaml          # 全螢幕遮罩視窗
│       │   └── SnipWindow.axaml.cs
│       └── Services/
│           ├── IScreenCaptureService.cs  # 截圖服務介面
│           ├── ScreenCaptureService.cs   # 截圖服務實作
│           └── FFmpegEncoder.cs          # FFmpeg 編碼器
├── README.md
├── LICENSE
├── .gitignore
└── GimmeCapture.sln
```

---

### [NEW] [GimmeCapture.sln](file:///D:/Projects/GimmeCapture/GimmeCapture.sln)

解決方案檔案。

---

### [NEW] [GimmeCapture.csproj](file:///D:/Projects/GimmeCapture/src/GimmeCapture/GimmeCapture.csproj)

AvaloniaUI 專案檔，使用 .NET 8.0，包含必要的 NuGet 套件：
- `Avalonia` (11.x)
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `CommunityToolkit.Mvvm` (MVVM 工具套件，提供 Source Generator)
- `SkiaSharp` (用於影格處理)

---

### [NEW] [ViewModelBase.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/ViewModelBase.cs)

ViewModel 基底類別，繼承自 `ObservableObject`：

```csharp
public class ViewModelBase : ObservableObject
{
    // 提供 INotifyPropertyChanged 實作
    // 使用 [ObservableProperty] 特性自動產生屬性
}
```

---

### [MODIFY] [MainWindow.axaml](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Views/MainWindow.axaml)

將主視窗改為應用程式設定介面（Preferences UI），參考 Snipaste 風格：
*   **Layout**: 使用 `TabControl` 分頁管理。
*   **Tabs**:
    *   **一般 (General)**: 語言、開機啟動 (Placeholder)。
    *   **擷圖 (Snip)**: 邊框粗細、遮罩顏色/透明度。
    *   **輸出 (Output)**: 自動儲存路徑、檔名格式。
    *   **關於 (About)**: 版本資訊。
*   **Actions**: 在底部保留「開始截圖 (Snip)」按鈕以便測試，未來將移至 System Tray。

### [MODIFY] [MainWindowViewModel.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/MainWindowViewModel.cs)

新增設定相關屬性：
*   `BorderThickness` (double)
*   `MaskOpacity` (double)
*   `AutoSave` (bool)

---

### [NEW] [SnipWindowViewModel.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/SnipWindowViewModel.cs)

截圖視窗 ViewModel：

```csharp
public partial class SnipWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private Rect _selectionRect;
    
    [ObservableProperty]
    private bool _isSelecting;
}
```

---

### [NEW] [BabymetalTheme.axaml](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Styles/BabymetalTheme.axaml)

BABYMETAL 風格資源字典，定義：

| 資源名稱 | 色碼 | 用途 |
|---------|------|------|
| `BMRingRed` | `#E60012` | 主要強調色 |
| `GothicBlack` | `#121212` | 背景主色 |
| `FoxGold` | `#D4AF37` | 次要強調色 |
| `PanelGray` | `#1E1E1E` | 面板背景 |

包含金屬感按鈕樣式 (`MetalButton`)。

---

### [NEW] [SnipWindow.axaml](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Views/SnipWindow.axaml)

全螢幕透明遮罩視窗：

- `TransparencyLevelHint="Transparent"`
- `SystemDecorations="None"`
- `WindowState="Maximized"`
- `Topmost="True"`
- 背景色：`#44000000`（半透明黑）
- 包含 `Canvas` 用於繪製矩形選區

---

### [NEW] [AppSettings.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Models/AppSettings.cs)

設定資料模型：
```csharp
public class AppSettings
{
    public string Language { get; set; } = "zh-TW";
    public bool RunOnStartup { get; set; }
    public bool AutoCheckUpdates { get; set; }
    
    // Snip
    public double BorderThickness { get; set; } = 2.0;
    public double MaskOpacity { get; set; } = 0.5;
    public string BorderColorHex { get; set; } = "#E60012";
    
    // Output
    public bool AutoSave { get; set; }
    public string SaveDirectory { get; set; }
    
    // Hotkeys
    public string SnipHotkey { get; set; } = "F1";
}
```

### [NEW] [AppSettingsService.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Services/AppSettingsService.cs)

負責 `config.json` 的讀取與寫入 (System.Text.Json)。

### [MODIFY] [MainWindow.axaml](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Views/MainWindow.axaml)

新增「控制」分頁，顯示快捷鍵設定。

### [MODIFY] [MainWindowViewModel.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/MainWindowViewModel.cs)

*   注入 `AppSettingsService`。
*   載入設定到屬性。
*   儲存設定時寫回 `config.json`。

---

### [NEW] [SnipToolbar.axaml](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Views/Controls/SnipToolbar.axaml)

選區下方的浮動工具列：
- 包含按鈕：Copy, Save, Close
- 樣式：金屬風格 (`MetalButton`)
- 位置：動態跟隨 `SelectionRect`

---

---

## 第二階段：跨平台錄影準備 (Starlight - GIF/Video Prep)

### [NEW] [GlobalHotkeyService.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Services/GlobalHotkeyService.cs)

實作全域快捷鍵服務 (Windows Only for Phase 1)。
*   使用 P/Invoke 呼叫 `RegisterHotKey` / `UnregisterHotKey`。
*   使用 Win32 Subclassing (`SetWindowLongPtr` GWLP_WNDPROC) 攔截 `WM_HOTKEY` 訊息。
*   提供 `Register(string hotkey)` 方法，自動解析字串 (e.g., "F1", "Ctrl+S")。

### [MODIFY] [MainWindowViewModel.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/MainWindowViewModel.cs)

整合 `GlobalHotkeyService`：
*   在 `LoadSettingsAsync` 後註冊快捷鍵。
*   監聽 `SnipHotkey` 屬性變更，重新註冊快捷鍵。
*   當收到快捷鍵事件時，觸發 `RequestCaptureAction`。

---

### [NEW] [FFmpegEncoder.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Services/FFmpegEncoder.cs)

FFmpeg 編碼器類別：

```csharp
public class FFmpegEncoder : IDisposable
{
    // 透過 Stdin 管道將 SKBitmap 影格傳送給 FFmpeg
    // 支援 GIF 和影片輸出
    // 使用 async/await 避免 UI 凍結
    
    Task StartRecordingAsync(string outputPath, int fps);
    Task EncodeFrameAsync(SKBitmap frame);
    Task StopRecordingAsync();
}
```

---

## 第三階段：開源社群營運 (GitHub Setup)

### [NEW] [README.md](file:///D:/Projects/GimmeCapture/README.md)

```markdown
# 🦊 GimmeCapture!!

**The Metal Image/Video Snip Tool**

> Inspired by BABYMETAL

跨平台截圖/錄影工具...
```

---

### [NEW] [LICENSE](file:///D:/Projects/GimmeCapture/LICENSE)

MIT License - 最自由且對開發者友好的開源協議。

---

### [NEW] [.gitignore](file:///D:/Projects/GimmeCapture/.gitignore)

標準 .NET / Visual Studio gitignore。

---

## 驗證計畫

### 自動化測試

```powershell
# 建置專案
dotnet build D:\Projects\GimmeCapture\GimmeCapture.sln

# 執行應用程式
dotnet run --project D:\Projects\GimmeCapture\src\GimmeCapture\GimmeCapture.csproj
```

### 手動驗證

1. 啟動應用程式，確認主視窗顯示正確
2. 觸發截圖功能，確認透明遮罩視窗正確顯示
3. 拖曳滑鼠繪製選區，確認紅色邊框正確顯示
4. 確認 UI 樣式符合 BABYMETAL 視覺風格
