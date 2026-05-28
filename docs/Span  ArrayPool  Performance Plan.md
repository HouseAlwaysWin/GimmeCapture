# Span / ArrayPool 效能優化分析結論

## Summary
- 這個專案最值得用 `Span<T> / ArrayPool<T> / unsafe + Span 包裝` 的地方，不是 UI、ViewModel 或設定物件，而是三條真正的熱路徑：
  - 像素級影像處理
  - 錄影 / 播放 frame buffer pipeline
  - OCR / AI 前處理與中間 buffer
- 最高報酬的優化不是「到處換 Span」，而是：
  - 拿掉逐像素 `GetPixel/SetPixel`
  - 拿掉 `MemoryStream + ToArray()`
  - 拿掉每 frame / 每 chunk 的新 `byte[]`
  - 把小型暫存與中型工作 buffer 換成 `ArrayPool<T>`
- `ref struct` 只適合很小、短生命週期、純計算 helper；這個專案大多數情況不需要它。真正有價值的是 `Span<T>`、`ReadOnlySpan<T>`、`ArrayPool<T>`，以及在像素熱路徑中搭配 `unsafe`。

## 可優化區域
| 區域 | 目前問題 | 為什麼適合 Span / Pool | 預估收益 | GC 改善 | stackalloc | ArrayPool | 風險 |
|---|---|---|---|---|---|---|---|
| `AnnotationRenderService` 的 `Mosaic / Blur / AverageCellColor` | 大量 `GetPixel/SetPixel`、ROI 內雙重迴圈、`target.Copy()`/區域 bitmap 配置 | 可直接把 `SKBitmap.GetPixels()` 包成 row `Span<uint>` 或 `Span<SKColor32>`，避免 method-call per pixel | 高 | 高 | 低 | 中 | 需要 `unsafe`，像素格式要非常一致 |
| `FloatingBitmapConversionHelper` 的 `MemoryStream + ToArray()` encode/decode | 每次轉圖都會額外配置 `MemoryStream` 與複製整份 `byte[]` | `Span<T>` 本身幫助有限，但很適合改成 pooled buffer 或直接像素 copy，不走 PNG roundtrip | 高 | 高 | 低 | 高 | 可維護性要顧，不能把 bitmap 生命週期搞混 |
| `FloatingVideoViewModel.Media` frame pipeline | `_latestFrameData` copy、`FrameStreamWriter` 每 frame staging、`Marshal.Copy` 到 UI bitmap | incoming frame chunk 很適合 `Span<byte>` 切片；整個 frame buffer 適合 `ArrayPool<byte>` 重複使用 | 很高 | 很高 | 低 | 很高 | 多執行緒 / double-buffer / 歸還時機要準確 |
| `LibavPinAudioPcmDecoder` | `List<byte[]>` 收 chunk，最後再合併成大 `byte[]` | 適合 pooled growable buffer 或 `IMemoryOwner<byte>`，append 用 `Span<byte>` | 中高 | 高 | 低 | 高 | 邊界與最終長度管理較麻煩 |
| `PaddleOCREngine` detection / recognition tensor 建構 | `resized.GetPixel()` 雙重迴圈、`Enumerable.ToArray(mask)`、`float[,]`/`bool[,]` 臨時配置 | 像素遍歷與 mask flatten 很適合 `Span<T>`；二值 map / visited 可改 pooled 一維陣列 | 很高 | 高 | 低 | 高 | 可讀性會下降，需要明確註解 |
| `AIScanSessionService` OCR scan 結果投影 | LINQ 鏈 `Select/Where/ToList` | 可用預估容量 `List<T>` + for-loop，少掉 iterator allocation | 中 | 中 | 不需要 | 可選 | 收益不如像素路徑大 |
| `WindowsGlobalHotkeyService.ParseHotkey` | `ToUpper`、`Contains`、`Split`、`Substring` | 非常適合 `ReadOnlySpan<char>` slicing，避免短字串 allocation | 中 | 中 | 適合 | 不需要 | 邏輯要小心處理大小寫與別名 |
| `TranslationService` 文字合併 / script 檢查 | `string.Join(...ToArray())`、多次 LINQ | 適合 `StringBuilder`、for-loop、`ReadOnlySpan<char>` helper | 低到中 | 中 | 適合小字串解析 | 不需要 | 收益比影像處理小 |
| `FloatingImageViewModel.AI` 遮罩輸出 | 多次 `Encode(...).ToArray()`、mask bitmap roundtrip | 適合優先「避免 encode/decode」，其次才是 pooled buffer | 高 | 高 | 低 | 高 | 比 Span 更像資料流重構 |

## 不建議硬改的地方
- `UI Model / ViewModel / UndoRedo / Annotation object`
  - 不適合 `ref struct`，因為它們要被綁定、持久化、放集合、跨 async/observable 傳遞。
- `HotkeyRouterService`
  - 幾乎都是 switch 與字串比較，不是 allocation 熱點，除非 profiler 證明它很熱，不然不用為它引入複雜 `Span` 寫法。
- `Skia Resize / Blur native call` 本身
  - 真正重的是 Skia/FFmpeg/ONNX native 執行，`Span` 幫不到內部演算法，只能優化你餵進去與拿出來的 buffer。
- `ref struct` 大範圍導入
  - 這個專案不需要把 bitmap wrapper、ROI model 或 OCR candidate 改成 `ref struct`；收益低，限制很多。

## 建議實作順序
1. `AnnotationRenderService`
   - 把 `Mosaic` 和 `AverageCellColor` 改成直接走 pixel buffer span。
   - `Blur` 先只優化 ROI copy-in/copy-out，不急著自己手寫卷積。
2. `FloatingVideoViewModel.Media`
   - `FrameStreamWriter`、`QueueLatestFrame`、UI refresh path 改成 pooled frame buffers。
3. `PaddleOCREngine`
   - detection / recognition 前處理改成 span-based pixel traversal。
   - `TryBuildDetProbabilityMap` 去掉 `Enumerable.ToArray(mask)` 與 2D 陣列。
4. `FloatingBitmapConversionHelper` / `FloatingImageViewModel.AI`
   - 優先移除 encode/decode roundtrip。
5. `WindowsGlobalHotkeyService.ParseHotkey`
   - 用 `ReadOnlySpan<char>` 做低風險 allocation cleanup。

## 建議修改範例
```csharp
// 1. Hotkey parser: 用 ReadOnlySpan<char> 避免 Split/Substring
private static (uint mods, uint vkey) ParseHotkey(ReadOnlySpan<char> hk)
{
    hk = hk.Trim();
    uint mods = 0;

    if (hk.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)) mods |= 0x0002;
    if (hk.Contains("Alt", StringComparison.OrdinalIgnoreCase)) mods |= 0x0001;
    if (hk.Contains("Shift", StringComparison.OrdinalIgnoreCase)) mods |= 0x0004;

    int plus = hk.LastIndexOf('+');
    ReadOnlySpan<char> keyPart = plus >= 0 ? hk[(plus + 1)..].Trim() : hk;

    if (keyPart.Length >= 2 && (keyPart[0] is 'F' or 'f') &&
        int.TryParse(keyPart[1..], out int fNum) &&
        fNum is >= 1 and <= 24)
    {
        return (mods, (uint)(0x70 + fNum - 1));
    }

    if (keyPart.Length == 1)
    {
        char c = char.ToUpperInvariant(keyPart[0]);
        if (char.IsLetterOrDigit(c)) return (mods, c);
    }

    return (mods, 0);
}
```

```csharp
// 2. Frame staging: 用 ArrayPool<byte> 取代 new byte[]
private byte[]? _frameBuffer;
private int _frameBufferLength;

private void EnsureFrameBuffer(int size)
{
    if (_frameBuffer != null && _frameBufferLength == size) return;

    if (_frameBuffer != null)
        ArrayPool<byte>.Shared.Return(_frameBuffer);

    _frameBuffer = ArrayPool<byte>.Shared.Rent(size);
    _frameBufferLength = size;
}

private void QueueLatestFrame(ReadOnlySpan<byte> frameData, int generation)
{
    lock (_latestFrameLock)
    {
        EnsureFrameBuffer(frameData.Length);
        frameData.CopyTo(_frameBuffer);
        _latestFrameGeneration = generation;
    }
}
```

```csharp
// 3. Mosaic: 直接用像素 span，避開 GetPixel/SetPixel
private static unsafe Span<uint> GetPixelSpan(SKBitmap bitmap)
{
    return new Span<uint>((void*)bitmap.GetPixels(), bitmap.Width * bitmap.Height);
}

private static unsafe void ApplyMosaicFast(SKBitmap target, int left, int top, int right, int bottom, int cellSize)
{
    var pixels = GetPixelSpan(target);
    int width = target.Width;

    for (int y = top; y < bottom; y += cellSize)
    {
        for (int x = left; x < right; x += cellSize)
        {
            int cellRight = Math.Min(right, x + cellSize);
            int cellBottom = Math.Min(bottom, y + cellSize);

            ulong a = 0, r = 0, g = 0, b = 0;
            int count = 0;

            for (int yy = y; yy < cellBottom; yy++)
            {
                var row = pixels.Slice(yy * width + x, cellRight - x);
                foreach (uint px in row)
                {
                    b += (byte)(px);
                    g += (byte)(px >> 8);
                    r += (byte)(px >> 16);
                    a += (byte)(px >> 24);
                    count++;
                }
            }

            uint avg =
                ((uint)(a / (ulong)count) << 24) |
                ((uint)(r / (ulong)count) << 16) |
                ((uint)(g / (ulong)count) << 8) |
                (uint)(b / (ulong)count);

            for (int yy = y; yy < cellBottom; yy++)
            {
                var row = pixels.Slice(yy * width + x, cellRight - x);
                row.Fill(avg);
            }
        }
    }
}
```

```csharp
// 4. 小型固定暫存：stackalloc 適合這種微小常數資料
Span<int> dx = stackalloc int[4] { 0, 0, 1, -1 };
Span<int> dy = stackalloc int[4] { 1, -1, 0, 0 };
```

## 針對你列出的 5 類熱路徑結論
- 影像像素處理：最適合 `Span<T> + unsafe + ArrayPool<T>`，收益最大。
- 錄影 Frame Pipeline：最適合 `ArrayPool<byte>` 與 `ReadOnlySpan<byte>`，GC 改善最大。
- OCR / AI 前處理：非常適合 `Span<T>`，尤其 tensor 建構與 mask flatten。
- 字串與命令解析：適合 `ReadOnlySpan<char>`，但收益中等，屬於低風險 cleanup。
- 匯出與序列化：重點不是 `ref struct`，而是避免 `MemoryStream/ToArray` 與多次編碼拷貝。

## Assumptions
- 已採用你剛確認的方向：分析可以包含 `unsafe` 與指標型優化。
- 建議優先維持可維護性，所以只推薦在明確熱路徑中使用 `unsafe + Span`。
- `ref struct` 不作為主要優化手段；本專案主要用 `Span<T>`、`ReadOnlySpan<T>`、`ArrayPool<T>`、必要時 `MemoryMarshal` 即可。
- 如果下一步要落地，最值得先做的是 `AnnotationRenderService`、`FloatingVideoViewModel.Media`、`PaddleOCREngine` 三個點。
