# GimmeCapture!! 🦊 (Traditional Chinese)

這是一個極速、高效且充滿風格的螢幕擷取工具。名稱靈感來自於 BABYMETAL 的名曲 **["Gimme chocolate!!" (Official Video)](https://www.youtube.com/watch?v=WIKqgE4BwAY)**。

---

### 🎸 設計發想
**GimmeCapture!!** 的名稱追求極速、活力與強烈的視覺風格。整體的 UI 配色與主題皆致敬了 BABYMETAL 的經典美學。

### 功能特色
- **智慧擷圖**：高效能螢幕擷取，具備即時編輯工具。
- **螢幕錄影 + 系統音訊**：錄製畫面時可同時擷取桌面/系統聲音，支援 MP4、MKV、GIF 等格式。
- **翻譯模式**：內建 OCR + 翻譯流程，支援語言切換、框選區域與翻譯結果覆蓋顯示。
- **釘選視窗**：將擷圖化為浮動視窗，方便對照。
- **影片釘選播放器**：支援播放/暫停、循環、拖曳定位、倍率 (0.5x/1.0x/1.5x/2.0x) 與靜音切換。
- **編輯工具**：直接在擷圖上繪製框線、箭頭、直線與文字。
- **自訂快捷鍵**：所有熱鍵皆可在「控制」分頁中客製化。
- **個性化外觀**：可調整邊框粗細、遮罩透明度及主題配色 (金、銀、紅)。
- **裝飾比例調整**：可自訂**側邊翅膀 (0.5x - 3.0x)** 與**角落圖標 (0.4x - 1.0x)** 的大小。
- **自動啟動**：支援 Windows 開機後自動執行。
- **即時音訊監看**：錄影工具列可顯示輸入/輸出音量與 dB 狀態。

### 使用說明
1. 啟動後可從工具列切換三種模式：**擷圖 / 錄影 / 翻譯**。
2. 進入擷圖模式後框選範圍，即可直接註記、複製、儲存或釘選為浮動視窗。
3. 進入錄影模式後可開始、暫停、停止錄影，並在工具列即時查看音訊輸入/輸出電平。
4. 進入翻譯模式後設定輸入/輸出語言，框選文字區域後執行翻譯或 OCR 掃描。
5. 釘選視窗可右鍵開啟功能選單；影片釘選支援播放控制、倍率調整與音訊切換（預設靜音）。

### 翻譯模式補充
- 可從工具列的翻譯圖示切換到 **翻譯模式**。
- 設定輸入語言與輸出語言後，可直接框選一個或多個文字區域。
- 「框選時按住修飾鍵」可在 **設定 > 快捷鍵** 調整（`Shift` / `Ctrl` / `Alt` / `None`），預設為 `Ctrl`。
- 可用 **Translate All** 一次翻譯全部選取區，或用 **Scan All** 僅做 OCR 掃描。
- 翻譯結果覆蓋層可在翻譯工具列中切換顯示/隱藏。

### 錄影 / 影片釘選補充
- 是否擷取系統音訊可在 **設定 > 錄影** 中切換。
- 影片釘選模式中，音訊為**預設靜音**。
- 影片釘選模式中，可用 **Shift + M** 切換靜音/開聲。
- 調整影片播放倍率時，音訊也會同步套用對應倍率。

### 📦 第三方組件
- **FFmpeg**：用於錄影與多媒體處理。FFmpeg 採用 [GPL/LGPL](https://ffmpeg.org/legal.html) 授權。螢幕錄製透過 **libav*** DLL（[FFmpeg.AutoGen](https://www.nuget.org/packages/FFmpeg.AutoGen)）；後製轉檔／預覽仍會呼叫與 DLL 一併放在 `ffmpeg-lib/` 的 `ffmpeg.exe`、`ffprobe.exe`、`ffplay.exe`。發行前請執行 `powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1`，從 [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) 的 **win64-gpl-shared** 套件解出檔案。
- **NAudio**：用於錄影時的系統音訊迴路擷取，以及即時音量監看。

---

## 🛠️ 系統需求
- Windows 10/11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## ⚖️ 授權條款
採用 [MIT License](LICENSE) 授權。由 HouseAlwaysWin 開發。
