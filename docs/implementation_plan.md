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

### [NEW] [MainWindowViewModel.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/MainWindowViewModel.cs)

主視窗 ViewModel：

```csharp
public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _statusText = "Ready";
    
    [RelayCommand]
    private void StartCapture() { /* 開啟 SnipWindow */ }
}
```

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

### [NEW] [SnipWindow.axaml.cs](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Views/SnipWindow.axaml.cs)

實作滑鼠事件與 Snipaste 風格互動：

```csharp
// 互動狀態機
enum SnipState { Idle, Detecting, Selecting, Selected, Editing }

// 功能需求
1. **全域預覽** (Idle): 游標移動時顯示輔助線/座標
2. **自動視窗偵測** (Detecting): 
   - 根據游標位置自動偵測視窗邊界 (需實作 IWindowFinder)
   - 高亮顯示被偵測的區域
3. **滑鼠選取** (Selecting): 拖曳繪製矩形
4. **工具列** (Selected): 
   - 選取完成後，於選區下方顯示工具列
   - 功能：複製、儲存、取消、繪圖工具(二期)
5. **遮罩渲染**: 
   - 使用 Path/GeometryClip 實作「挖洞」效果
   - 選區亮，背景暗
```

矩形邊框使用 `BMRingRed` 色彩。

---

### [NEW] [SnipToolbar.axaml](file:///D:/Projects/GimmeCapture/src/GimmeCapture/Views/Controls/SnipToolbar.axaml)

選區下方的浮動工具列：
- 包含按鈕：Copy, Save, Close
- 樣式：金屬風格 (`MetalButton`)
- 位置：動態跟隨 `SelectionRect`

---

---

## 第二階段：跨平台錄影準備 (Starlight - GIF/Video Prep)

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
