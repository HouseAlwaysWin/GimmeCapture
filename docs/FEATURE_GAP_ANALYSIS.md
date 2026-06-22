# GimmeCapture 功能缺口分析報告

> 產出日期：2026-06-22
> 範圍：對 `src/`、`docs/` roadmap 與實際程式碼的探查，盤點「目前還缺少／需要實作或加強」的功能。
> 性質：分析報告，**不含程式碼變更**。後續實作待就本報告挑選項目後另起。

---

## 0. 摘要（Executive Summary）

GimmeCapture 是一個 Windows-only 的 Avalonia 12 / .NET 10 截圖工具，涵蓋
**Snip / Record / Translate / Pin** 四大模式，並支援可下載的本地 AI 模組
（PaddleOCR、SAM2、U2Net 背景移除、LlamaSharp 翻譯）。整體功能豐富、翻譯與 AI
工作流相對成熟。

但盤點後發現缺口分布在三個層級：

1. **一個高風險的正確性 Bug**：暫停/續錄後的多分段錄影只會保留第一段，**其餘內容被靜默丟棄**。
2. **數個 UI 佔位指令**：UI 顯示按鈕但點了沒反應（如浮動影片的 Crop / Pin 選取）。
3. **維護者自己在 roadmap 標記為未完成**的重構、效能與測試工作（仍有大型多責任檔案、效能優化 0% 實作、關鍵服務缺特性測試）。

### 缺口熱力表

| 領域 | 完成度 | 關鍵缺口 |
|------|:---:|------|
| 擷取 Capture | ~75% | 無捲動截圖、延遲倒數 UI 陽春、剪貼簿僅 PNG+Bitmap |
| 標註 Annotation | ~70% | 無高亮筆、步驟編號、Crop、圖層 z-order |
| 錄影 Record | ~80% | **分段 concat 遺失資料**、暫停/續錄 UI 缺、影片 Crop 未實作 |
| OCR / 翻譯 | ~85% | 純本地、語言有限、無線上 fallback、僅純文字複製 |
| 分享 / 匯出 | ~40% | 僅本地存檔，無雲端 / 社群 / Email / 列印 / 多格式複製 |
| 錯誤處理 | ~75% | 失敗多記 log 未一致呈現、重試少、無磁碟空間檢查 |
| 更新 Update | ~60% | 需手動重啟套用、無自動套用 / channel / rollback |
| 無障礙 | ~50% | 無 screen reader、無高對比、鍵盤導覽未強化 |
| 測試 | ~65% | 關鍵服務缺特性測試、無端對端 / 錄影整合測試 |
| 打磨 / UX | ~70% | 無 toast、無拖放、無歷史搜尋、無批次操作 |

> 完成度為相對於成熟截圖工具（ShareX / Greenshot / Snipping Tool）的概略評估。

---

## 1. 🔴 正確性 Bug（最高優先 — 已用程式碼證實）

### 1.1 錄影多分段合併遺失資料 — ✅ 已修復（P0）
> 已於 `LibavMuxer.ConcatVideoSegments` 實作原生 remux concat，並在
> `MergeVideoSegmentsAsync` 串接所有分段（失敗時才退回第一段並記錄）。以下為原始問題描述。

**位置**：`src/GimmeCapture/Services/Core/Media/RecordingService.Finalize.cs:58-70`

`MergeVideoSegmentsAsync` 在分段數 > 1 時，原生 concat 尚未實作，**只回傳第一段**：

```csharp
private async Task<string> MergeVideoSegmentsAsync(IReadOnlyList<string> validSegments, string mergedMkvPath)
{
    if (validSegments.Count == 1)
        return validSegments[0];

    // Native concat is not available yet. Returning a real segment keeps
    // finalization functional after pause/resume instead of referencing a
    // merged path that was never created.
    Debug.WriteLine("[Finalize] Native concat pipeline pending; using first valid segment.");
    return validSegments[0];   // ⚠️ 後續片段被丟棄
}
```

- **影響**：暫停/續錄是常見操作，每次續錄都會產生新分段；最終輸出只含第一段，使用者實際會遺失錄影內容，且**無錯誤提示**（僅 `Debug.WriteLine`）。
- **可重用基礎建設**：音訊合併（同檔 `MergeAudioSegmentsAsync`，72 行起）已用 NAudio 正確實作，**僅影片端是 stub**。專案已有 libav 封裝可用來補影片 concat：
  - `src/GimmeCapture/Services/Core/Media/NativeFFmpeg/LibavMuxer.cs`
  - `src/GimmeCapture/Services/Core/Media/NativeFFmpeg/LibavGdigrabMkvSession.cs`
- **建議**：以 libav 實作多段 MKV concat（demux→remux，避免重編碼），或在 concat 完成前先以 UI 警示阻擋（暫時緩解）。

### 1.2 空的 UI 指令（佔位）— ✅ 已修復
> `CropCommand`/`PinSelectionCommand` 已實作：選取區域 → 以 ffmpeg `crop`（沿用 trim）
> 匯出 → 開新釘選視窗（Crop 關閉來源、PinSelection 保留）；canExecute 綁定
> `IsSelectionActive`。另記：暫停/續錄 UI 本就存在（`SnipToolbar.axaml`）。以下為原始描述。

**位置**：`src/GimmeCapture/ViewModels/Floating/FloatingVideoViewModel.Actions.cs:33-35`

```csharp
// Placeholders for now
CropCommand = ReactiveCommand.Create(() => { });
PinSelectionCommand = ReactiveCommand.Create(() => { });
```

對應宣告：`FloatingVideoViewModel.cs:311-312`（`// Future implementation`）。

- **影響**：浮動影片視窗的「裁切」與「釘選選取」按鈕在 UI 上可見，但點擊無任何效果，造成使用者困惑。
- **建議**：實作影片裁切（可重用既有 `IsTrimmingMode` / `ExportBurntInVideoAsync` 的後製管線思路）與選取釘選；在完成前先隱藏或停用按鈕。

---

## 2. 🟠 優先領域一：錄影穩定性

**現況（已實作）**：H.264/H.265、品質分級、MP4/MKV/GIF/WebM/MOV 轉檔
（`NativeFFmpeg/Libav*Transcoder.cs`）、WASAPI 系統音訊（`RecordingService.Audio.cs`）、
`AudioLevelMonitorService.cs`、`RecordingState`（Idle/Recording/Paused）列舉、
錄影中即時標註工具列。

**缺口 / 需加強**：

| 項目 | 現況 | 建議 |
|------|------|------|
| 影片分段 concat | ✅ 已修復（`LibavMuxer.ConcatVideoSegments`） | — |
| 暫停 / 續錄 | ✅ 已存在（`SnipToolbar.axaml` 按鈕 + `PauseRecordingCommand` toggle） | concat 修復後才真正可用 |
| 影片 Crop / PinSelection | ✅ 已實作（§1.2） | — |
| 錄影中音量回饋 | `AudioLevelMonitorService` 已有資料 | UI 加上音量條視覺化 |
| Webcam 子母畫面 | 無 | 中長期功能 |
| 游標 highlight / 聚光 | 僅標準游標（`ShowRecordCursor`） | 教學錄影常用 |
| 硬體編碼器自動偵測 | 僅 GDI grab + 基本編碼器 | 偵測 NVENC/QSV/AMF |
| 異常中斷清理 | 不保證 segment 清理 | 啟動時清理孤兒 temp |

---

## 3. 🟠 優先領域二：標註工具擴充

**現況**（`src/GimmeCapture/Models/Annotation.cs` +
`src/GimmeCapture/Services/Core/Rendering/AnnotationRenderService.cs`）：
矩形、橢圓、箭頭、線、文字、自由筆（pen/polyline）、Mosaic、Blur、Undo/Redo
（`HistoryAction`）、顏色與粗細調整。

**缺口 / 需加強**：

- **高亮筆**：缺半透明 highlighter 覆蓋工具。
- **步驟編號 / callout**：procedural 截圖常用的自動編號圓圈與引線標記。
- **截圖後 Crop**：選取已存在，但無後製裁切 UI。
- **圖層 z-order**：所有標註依清單順序繪製，無 bring-to-front / send-to-back。
- **形狀變換**：無旋轉 / 縮放既有圖形的控制點。
- **Emoji / 貼圖**：無插入支援。
- **Feather 整合**：`AnnotationEffectSettings.Feather` 已存在但 UI 未完整整合。

> 效能注意：`AnnotationRenderService` 的 Mosaic/Blur 目前逐像素 GetPixel/SetPixel，
> 擴充標註功能時建議一併處理（見 §4 效能）。

---

## 4. 🟠 優先領域三：架構重構 + 測試

依 `docs/ARCHITECTURE_REFACTOR_ROADMAP.md` 與 `docs/REFACTOR_PLAN.md`。

**已完成**：Hotkey 路由集中化（`HotkeyIds`/`HotkeyRouterService`/`HotkeyMappingService`）、
Translation/OCR 服務拆分、Snip 大型 ViewModel partial 拆檔、UI 解耦
（`IWindowManager`/`IThemeResourceService`/`IScreenLayoutService` 等）、
`Composition/AppBootstrapper.cs` 已導入（roadmap 撰寫時尚無，現已存在）。

**仍未完成 / 需加強**：

| 項目 | 現況 | 目標 |
|------|------|------|
| Settings/Hotkey 邊界 | `MainWindowViewModel.Settings.cs` 約 1011 行，混合 UI / 持久化 / hotkey 註冊 | 拆出持久化與註冊協調器 |
| Snip session 編排 | 仍留在 `SnipWindowViewModel.*`（`Selection.State.cs` 約 992 行） | 抽出 session controller / state machine |
| AI 資源編排 | `AIResourceService` 約 855 行仍有 drift | 持續拆分 catalog/installer/runtime/queue |
| App Shell / Tray | 分散在 `App` 與 `MainWindow` code-behind | 抽出 TrayController / AppShellService |

**測試缺口（roadmap 自述）**：

- 缺特性測試的關鍵單元：`MainWindowViewModel`、`ResourceQueueService`、
  `TranslationService`、app-shell/tray 編排。
- 無端對端（capture→annotate→save）與錄影整合測試。
- 覆蓋率門檻僅 **25%**（`scripts/verify.ps1`）。
- **建議**：在動大型重構前，先為上述服務補特性測試作為安全網（roadmap Phase 0）。

**效能（`docs/Span ArrayPool Performance Plan.md`，目前 0% 實作）**：

1. `AnnotationRenderService` 的 Mosaic/Blur 逐像素 GetPixel/SetPixel → 改用 `Span<T>` + 不安全像素緩衝（GC 影響最大，優先）。
2. `FloatingVideoViewModel.Media` 每幀 `new byte[]` / `MemoryStream.ToArray()` → `ArrayPool<byte>`。
3. `PaddleOCREngine` 張量前處理的 2D 陣列 / `ToArray()` → `Span<T>`。
4. `FloatingBitmapConversionHelper` 每次轉換的 PNG encode/decode roundtrip → 移除或改用 pooled buffer。

---

## 5. 🟡 其他領域缺口（完整覆蓋，較低優先）

### 5.1 擷取 Capture
- 無捲動截圖（scrolling capture / stitching）。
- 延遲擷取：`CaptureDelay` 列舉存在但倒數 UI 較陽春。
- 剪貼簿僅 PNG + Bitmap（`ClipboardService.cs`）；缺 JPEG/BMP/TIFF/HTML。
- 無一鍵全螢幕擷取、無選取時自動吸附視窗邊緣。

### 5.2 OCR / 翻譯
- 純本地推論，無線上 fallback（Google Vision / Azure 等）。
- 語言有限（約 5 + Auto），不及主流工具的數十種語言。
- 無批次 OCR；OCR 結果僅純文字複製，無 RTF/Markdown。
- 不支援使用者自訂 OCR 模型。

### 5.3 分享 / 匯出（完成度最低，~40%）
- 僅本地存檔（`SaveDirectory`/`VideoSaveDirectory`）+ 存檔後開啟資料夾。
- 缺：雲端上傳（OneDrive/GDrive/Dropbox）、Imgur/Gyazo、分享連結、Email、
  社群直發、列印、多格式複製到剪貼簿、多檔 ZIP 匯出。

### 5.4 錯誤處理 / 健壯性
- 失敗多記 log（`AppLog`）但未一致呈現給使用者。
- 重試邏輯少，多為 fail-and-fallback。
- 無擷取前磁碟空間檢查、下載逾時不可設定、無啟動時 FFmpeg 可用性早期檢查。

### 5.5 更新 Update
- `UpdateService.cs` 可檢查 / 下載 / SHA256 驗證，但**需手動重啟套用**。
- 無自動套用、無背景檢查頻率設定、無 delta 更新、無 rollback、無 beta channel。

### 5.6 無障礙
- 無 screen reader 支援、無高對比模式、鍵盤導覽 / focus 指示未強化、錄影無字幕。

### 5.7 打磨 / UX
- 無擷取/存檔完成音效與 toast 通知、無拖放輸出、無歷史搜尋與批次操作、
  Undo 不跨 session、播放無縮放。

### 5.8 發佈工程
- `docs/release-smoke-matrix.md` 為手動測試矩陣，無自動化煙霧測試。

### 5.9 AI 模組清理
- `src/GimmeCapture/Services/Core/AI/AIModelCatalog.cs:59-66` 仍有 placeholder 與
  deprecated 模型殘留（`gemma-4-placeholder`、多個 `DeprecatedPreset(...)`）。雖有遷移處理，但屬程式碼雜訊，可清理。

---

## 6. 建議的實作優先順序（Roadmap）

| 優先 | 項目 | 重用 / 參考 |
|:---:|------|------|
| **P0** | 修復影片分段 concat 資料遺失 | `NativeFFmpeg/LibavMuxer.cs` |
| **P0** | 補暫停/續錄 UI 綁定 + 影片 Crop 指令 | `SnipWindowViewModel.Recording.cs`、`FloatingVideoViewModel` |
| **P1** | 標註工具擴充（高亮筆、步驟編號、Crop、z-order） | `AnnotationRenderService.cs`、`Annotation.cs` |
| **P1** | 為錄影 / Translation / ResourceQueue 補特性測試 | `tests/GimmeCapture.Tests/` |
| **P2** | Settings/Hotkey 與 App Shell 重構；`AnnotationRenderService` Span 效能 | roadmap / 效能計畫 |
| **P2** | 分享 / 匯出（雲端、多格式複製）、自動更新套用、錯誤 UX | — |

---

## 附錄：本報告引用的程式碼位置

- `src/GimmeCapture/Services/Core/Media/RecordingService.Finalize.cs:58-70`
- `src/GimmeCapture/ViewModels/Floating/FloatingVideoViewModel.Actions.cs:33-35`
- `src/GimmeCapture/ViewModels/Floating/FloatingVideoViewModel.cs:311-312`
- `src/GimmeCapture/Services/Core/AI/AIModelCatalog.cs:59-66`
- `docs/ARCHITECTURE_REFACTOR_ROADMAP.md`、`docs/REFACTOR_PLAN.md`、`docs/Span  ArrayPool  Performance Plan.md`、`docs/release-smoke-matrix.md`
