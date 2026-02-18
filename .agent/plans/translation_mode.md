# 實作新翻譯模式 (Translation Mode) - 多重選取與 Google Lens 風格

本計畫旨在新增一個功能強大的翻譯模式，結合 Google Lens 式的文字偵測、多重手動選取區域以及右鍵取消功能。

## 使用者需求確認
- [x] **獨立模式**：按下 `F3` 進入，無黑色遮罩（螢幕不變暗）。
- [x] **Google Lens 風格偵測**：進入後自動掃描全螢幕文字，並以高亮框顯示。
- [x] **多重選取支援**：
  - [x] 使用者可以手動拉取多個選取框。
  - [x] 現有的選取邏輯僅支援單一框，需擴展為集合管理。
- [x] **右鍵取消功能**：在選定區域上點擊右鍵可彈出選單或直接取消該選取區。
- [x] **原地翻譯**：翻譯結果覆蓋在選取區上方。

## 擬議變更

### [GimmeCapture.Models]

#### [MODIFY] [MainWindowViewModel.cs](file:///d:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/Main/MainWindowViewModel.cs)
- 在 `CaptureMode` 中新增 `Translate` 成員。
- 註冊 `F3` 全域熱鍵。

#### [MODIFY] [SnipWindowViewModel.cs](file:///d:/Projects/GimmeCapture/src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.cs)
- 新增 `IsTranslationMode` 屬性。
- 新增 `UserSelections` (ObservableCollection<TranslatedBlock>)，用於管理多個手動或自動選取的區塊。
- 實作 `RemoveSelectionCommand`：接受一個 `TranslatedBlock` 並從集合中移除。

### [GimmeCapture.Views]

#### [MODIFY] [SnipWindow.axaml](file:///d:/Projects/GimmeCapture/src/GimmeCapture/Views/Main/SnipWindow.axaml)
- 隱藏翻譯模式下的遮罩。
- **[NEW]** 多重選取圖層：使用 `ItemsControl` 綁定 `UserSelections`。
  - 每個 Item 使用 `Border` 顯示選取框。
  - 為 `Border` 添加 `ContextMenu`，包含「取消選取」選項。
- **[NEW]** 原地翻譯圖層：同樣使用 `ItemsControl` 於上方顯示翻譯文字。

#### [MODIFY] [SnipWindow.Pointer.cs](file:///d:/Projects/GimmeCapture/src/GimmeCapture/Views/Main/SnipWindow.Pointer.cs)
- 在翻譯模式下，`OnPointerPressed` 不再清空舊選取，而是開始建立新的選取塊。
- 處理右鍵點擊事件，偵測是否點擊在現有選取塊上，並顯示 ContextMenu。

#### [MODIFY] [SnipToolbar.axaml](file:///d:/Projects/GimmeCapture/src/GimmeCapture/Views/Controls/SnipToolbar.axaml)
- 根據 `IsTranslationMode` 顯示工具列，包含「翻譯全部」、「清除所有選取」等按鈕。

## 驗證計畫

### 手動驗證
1. 按下 `F3` 進入翻譯模式。
2. 隨意拉取 3 個不同的選取框。
3. 確認 3 個選取框同時存在於螢幕上。
4. 對其中一個選取框點擊右鍵並選擇「取消」，確認該框消失。
5. 點擊「翻譯全部」，確認所有選取框位置都出現了翻譯文字。
