# Linux 移植可行性分析 (Linux Port Feasibility)

本文件評估把 **GimmeCapture**（Avalonia 12 / .NET 10、目前 `net10.0-windows10.0.19041.0` 的
Windows-only 螢幕擷取工具）移植到 Linux 的可行性、阻力點與工作量分級。定位為 **核心子集優先**：
先讓 Snip / 標註 / Pin / 檔案式壓縮在 Linux 跑起來，即時錄影、系統音訊、全域熱鍵延後。

> ✅ 現況（2026-07-17 更新）：**Phase 0/1/2 全部完成並自 v0.51.0 出貨** — 本文件其餘內容為
> 移植前的可行性快照，僅供歷史參考。Linux (X11) 版已涵蓋完整功能集：libX11 靜態擷取、
> `XGrabKey` 全域熱鍵、`x11grab` 錄影、PulseAudio 系統/麥克風音訊、V4L2 webcam、
> 捲動截圖、跨平台自動更新，release 並隨附 `GimmeCapture_linux-x64.tar.gz`。
> 唯一維持 Windows 專屬的是 **WGC 逐視窗錄影**（Linux 無等價 API，維持隱藏）。
> 尚缺的是**自動化驗證**：測試專案仍單一目標 `net10.0-windows`，`net10.0` head 沒有任何測試 gate。

---

## 結論（TL;DR）

| 面向 | 判斷 | 說明 |
|---|---|---|
| **核心子集**（Snip / 標註 / Pin / 檔案壓縮） | ✅ 可行，工作量中等 | 多數已跨平台或已在介面後面；主要缺一個 Linux `IScreenCaptureService` 實作 + 建置 shape 調整 + 原生相依換 Linux 版。 |
| **完整功能對等** | ⚠️ 工作量大 | 即時錄影 demuxer（gdigrab/dshow）、系統音訊（WASAPI）、全域熱鍵都要換 Linux 後端。 |
| **WGC 逐視窗錄影** | ❌ 硬卡死 | Windows.Graphics.Capture 無 Linux 等價 API，該功能在 Linux 直接砍掉，錄影退回全螢幕/區域路徑。 |

架構上有利：`Services/Platforms/{Windows,Avalonia}` 分層已在，多數 UI 走 `Services/Abstractions/`
介面（透過 `Composition/RuntimeServiceFactory.cs`），所以「新增一個 Linux 平台資料夾」的接縫大致就位。
硬阻力集中在三塊：**擷取管線、snip overlay 的 Win32 region/hook、建置與原生相依 shape**。

---

## 可攜性分級

### (A) 硬 Windows-only — 需全新後端或直接砍功能

| 子系統 | 檔案 | 依賴 |
|---|---|---|
| WGC 逐視窗 GPU 錄影 | `Services/Platforms/Windows/WgcInterop.cs`、`WgcWindowCaptureSource.cs`、`Services/Core/Media/NativeFFmpeg/LibavWgcMkvSession.cs`、`LibavWgcCompositeMkvSession.cs` | WinRT `Windows.Graphics.Capture` + D3D11/DXGI → **Linux 砍掉** |
| 全域熱鍵引擎 | `Services/Platforms/Windows/WindowsGlobalHotkeyService.cs` | `RegisterHotKey` + `WH_KEYBOARD_LL` 低階鉤 + Raw Input + 隱藏訊息視窗 + advapi32 完整性檢查 |
| 視窗列舉/幾何 | `Services/Platforms/Windows/WindowDetectionService.cs` | `EnumWindows` + dwmapi cloaked bounds + Registry 讀 Explorer |
| Snip overlay 點擊穿透 + 排除自我擷取 | `Services/Interop/Win32Helpers.cs`、`Views/Main/SnipWindow.Win32.cs`、`SnipWindow.Win32.Region.cs` | `SetWindowRgn` / `WDA_EXCLUDEFROMCAPTURE` / `SetWindowLongPtr` / `WM_NCHITTEST` |
| HWND 視窗樣式 | `Views/Main/ScrollingCaptureRegionWindow.cs`、`Views/Floating/FloatingImageWindow.axaml.cs`、`Views/Main/MainWindow.axaml.cs` | user32 window-long-ptr styling |
| UAC 提權 | `app.admin.manifest`（Release） | Windows 專屬 |
| 記憶體修剪 | `Services/Core/Infrastructure/ProcessMemoryTrimService.cs` | psapi `EmptyWorkingSet` |

### (B) Windows-only 但有明確替代方案

| 功能 | 現況檔案 | Linux 替代 |
|---|---|---|
| 靜態螢幕擷取 | `Services/Platforms/Windows/WindowsScreenCaptureService.cs`（GDI `CopyFromScreen`） | 寫 X11/PipeWire 或 libav `x11grab` 抓單張的 `IScreenCaptureService` 實作。介面已在；唯一 `new` 在 `Composition/RuntimeServiceFactory.cs:13-16`，需加 OS 分支 |
| gdigrab 桌面/區域錄影 | `Services/Core/Media/NativeFFmpeg/LibavGdigrabMkvSession.cs`（`av_find_input_format("gdigrab")`） | 同一份 FFmpeg.AutoGen 程式碼，改 `x11grab` / `kmsgrab` / `pipewiregrab` + 選項 |
| dshow 網路攝影機 | `WebcamCaptureSource.cs`、`VideoInputDevices.cs` | libav `v4l2` |
| 系統/麥克風音訊 + 播放 | `RecordingService.Audio.cs`、`AudioInputDevices.cs`、`AudioLevelMonitorService.cs`、`AudioPreviewPlayer.cs`、`WavMixer.cs`（NAudio WASAPI） | libav `pulse`/`alsa`/`pipewire`（已有 libav）或 PulseAudio/OpenAL 綁定。混音數學本身可攜 |
| 原生 FFmpeg 綁定 | `GimmeCapture.csproj:34-49`、`scripts/ensure-ffmpeg-libs.ps1`（只抓 win64 zip）、`FFmpegRuntime.cs`、`FFmpegBundledPaths.cs`（硬寫 `*.dll` 探測） | Linux `.so` GPL shared build + OS-aware 檔名 pattern |
| ONNX GPU provider | `Microsoft.ML.OnnxRuntime.DirectML`（`csproj:67`）+ 硬寫 `onnxruntime-win-x64-gpu` 下載（`AIModelCatalog.cs:122`、`AIPathService.cs:70`、`NativeResolverService.cs`） | CPU 或 CUDA Linux runtime。`OnnxProviderConfigurator.cs` 的 CPU fallback 已在，推論程式碼可攜 |
| 開機自動啟動 | `StartupService.cs`、`WindowsStartupRegistrationService.cs`（Registry `HKCU\...\Run`） | Linux `.desktop` autostart 檔（介面 `IStartupRegistrationService` 已在） |
| 剪貼簿 / 錯誤對話框 | WinForms `Clipboard`（已 `IsWindows()` 守衛）、`MessageBox`（`Program.cs`、`App.axaml.cs`、`UpdateService.cs`、`RecordingService.Finalize.cs`） | Avalonia `IClipboard` / Avalonia 對話框 |
| GDI 點陣圖轉換 | `WindowsScreenCaptureService.cs`（`System.Drawing.Imaging`） | SkiaSharp（已是相依） |
| 建置 shape | `Directory.Build.props:5`（`RuntimeIdentifiers=win-x64`）、TFM `net10.0-windows*`、`UseWindowsForms`、`WinExe` | 多目標 / 加 `linux-x64` RID |

### (C) 已跨平台 — 不用動

- **全部檔案輸入的 libav 轉碼/封裝**（吃路徑、不吃 OS device）：`LibavMuxer`、`LibavClipExporter`、
  `LibavAacTranscoder`、`LibavOpusTranscoder`、`LibavWebmTranscoder`、`LibavGifTranscoder`、
  `LibavAtempoFilter`、`LibavVideoFramePlayer`、`LibavPinAudioPcmDecoder`。
- **Avalonia UI 平台服務**：`AvaloniaWindowManager`（無 HWND）、`AvaloniaWindowLayerService`（`Topmost`）、
  `AvaloniaScreenLayoutService`（純幾何）。浮動/Pin 視窗 `Views/Floating/FloatingWindowBase.cs`（`BeginMoveDrag`/`Screens`/`Topmost`）。
- **原生對話框**：全用 Avalonia `StorageProvider`（無 WinForms 檔案對話框）。
- **儲存路徑**：`AppStoragePaths.cs` 用 `Environment.SpecialFolder.LocalApplicationData`（Linux → `~/.local/share`）。
- **熱鍵路由/解析純邏輯**：`HotkeyRouterService`、`HotkeyMappingService`、`HotkeyParsingHelper`、`HotkeyIds`。
- SkiaSharp 標註渲染、Serilog、CliWrap、ZLinq、`Microsoft.ML.Tokenizers`、**LLamaSharp + Cpu backend**（跨平台）、ONNX 推論程式碼。
- **Tray**：`TrayController.cs` 用 Avalonia `TrayIcon`（非 WinForms `NotifyIcon`；Linux 支援視桌面環境而定）。

---

## 唯一硬寫 Windows 型別的 DI 點

移植時要改分支的地方很集中：

- `Composition/RuntimeServiceFactory.cs` — `new WindowsScreenCaptureService(...)`、`new WindowDetectionService(...)`。
- `Composition/MainWindowViewModelDependenciesFactory.cs:22` — `new WindowsGlobalHotkeyService()`。

其餘 UI 服務都已經走介面，加 Linux 實作就能 slot in。

---

## 建議路線圖（核心子集優先）

**Phase 0 — 讓非 Windows 能連結/執行**
- 多目標化：保留 `net10.0-windows`，另加一個非 Windows TFM（或用 runtime 判斷），把 (A) 類 Windows-only 型別
  全部隔進 `Services/Platforms/Windows/` 且只在 Windows TFM 編譯。
- `Directory.Build.props` 加 `linux-x64` RID。
- 把 `UseWindowsForms` 的實際用途（Clipboard / MessageBox）改走 Avalonia，讓非 Windows 也能 link。

**Phase 1 — 核心可用**
- 寫 Linux `IScreenCaptureService` 實作（X11 / PipeWire 或 libav `x11grab` 抓單張）+ `RuntimeServiceFactory` OS 分支。
- 打通 Snip → 標註 → Pin → 檔案壓縮：libav 換 Linux `.so`；ONNX 換 CPU runtime（下載 URL / 檔名 pattern OS 化）。

**Phase 2 — 延後（完整對等才做）**
- 即時錄影：gdigrab → `x11grab`/`pipewire`。
- 系統音訊：WASAPI → `pulse`/`pipewire`。
- 全域熱鍵：X11 `XGrabKey` 或 desktop portal。
- Webcam：dshow → `v4l2`。
- **WGC 逐視窗錄影：Linux 標記為不支援。**

---

## 驗證限制（重要）

- 本專案的擷取 / 熱鍵 / 音訊需要**桌面環境**。純 headless 的 CI/cloud Linux 無顯示伺服器，
  這些多半只能 compile-check + 單元測試，真正的擷取行為要在有 X11/Wayland 桌面的 Linux 機器上實測。
- 檔案式路徑（libav 轉碼、標註渲染、AI 推論）可以在 Linux 用單元/整合測試驗證，不需桌面。
- 因此移植若真的展開，驗證會分兩軌：**可自動測的檔案流** vs. **需真機的擷取/互動流**。

---

## 一句話總結

移植**核心子集**（截圖 + 標註 + Pin + 檔案壓縮）到 Linux 是務實可行的中等工程，接縫大多已就位；
**即時錄影與系統音訊**要換後端、屬較大工程；**WGC 逐視窗錄影**在 Linux 無解，只能放棄。
建議若要動手，走 Phase 0 → Phase 1，先在 Linux 交付一個「擷取 + 標註 + Pin + 壓縮」的可用版本。
