using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.OCR;
using GimmeCapture.ViewModels.Floating;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    /// <summary>
    /// 翻譯模式：翻譯所有 UserSelections 中的圈選區域
    /// </summary>
    private async Task TranslateAllSelectionsAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[TranslationMode] TranslateAllSelectionsAsync triggered");
            
            if (IsTranslating)
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] Cancelling active translation...");
                _translationCts?.Cancel();
                // Do not set IsTranslating = false here, let the finally block handle it
                return;
            }

            _translationCts?.Cancel();
            _translationCts?.Dispose();
            _translationCts = new CancellationTokenSource();
            var token = _translationCts.Token;

            if (UserSelections.Count == 0)
            {
                // UX rule: translate button should only process explicit user selections.
                // If no selection exists, do nothing instead of scanning full screen.
                System.Diagnostics.Debug.WriteLine("[TranslationMode] No selections found. Skip translation.");
                _mainVm?.SetStatus("StatusTranslateNoSelection");
                return;
            }

        System.Diagnostics.Debug.WriteLine($"[TranslationMode] Proceeding to translate {UserSelections.Count} regions");
        IsTranslating = true;
        ShowTopLoadingBar = true;
        IsIndeterminate = true;
        ProcessingText = "Translating...";

        try
        {
            if (!await EnsureTranslationEngineReadyAsync()) return;
            var mainVm = _mainVm;
            var translationSession = _translationSession;
            if (mainVm == null || translationSession == null) return;

            // Ensure warm-up has completed before the first actual translation pass.
            StartTranslationWarmup();
            await AwaitTranslationWarmupAsync(token);

            // Ensure OCR resources
            bool ready = await translationSession.EnsureOcrReadyAsync(mainVm.SourceLanguage, token);
            if (!ready)
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] OCR not ready");
                return;
            }

            // 逐一翻譯每個選取區域
            var selectionsCopy = UserSelections.AsValueEnumerable().ToList();
            foreach (var sel in selectionsCopy)
            {
                if (token.IsCancellationRequested) break;
                // 用戶點擊按鈕時，無論是否翻譯過都強制重新翻譯



                IsCapturing = true;
                await Task.Delay(50); // 等待 UI 隱藏

                using var bitmap = await _captureService.CaptureScreenAsync(sel.Bounds, ScreenOffset, VisualScaling, false);
                
                IsCapturing = false;

                if (bitmap == null) continue;

                token.ThrowIfCancellationRequested();
                var (blocks, errorKey) = await Task.Run(
                    async () => await translationSession.AnalyzeAndTranslateAsync(
                        bitmap,
                        VisualScaling,
                        mainVm.SourceLanguage,
                        mainVm.TargetLanguage,
                        token),
                    token);
                
                if (token.IsCancellationRequested) break;

                // 合併所有翻譯結果作為這個區域的翻譯文字
                var combinedText = BuildCombinedBlockText(blocks, static block => block.TranslatedText);
                var combinedOriginalText = BuildCombinedBlockText(blocks, static block => block.OriginalText);
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    bool isFailed = string.IsNullOrWhiteSpace(combinedText);
                    if (isFailed)
                    {
                        var failMsgKey = string.IsNullOrEmpty(errorKey) ? "StatusTranslateFailed" : errorKey;
                        _mainVm?.SetStatus(failMsgKey);
                        sel.TranslatedText = LocalizationService.Instance[failMsgKey];
                    }
                    else
                    {
                        sel.TranslatedText = combinedText;
                    }

                    sel.OriginalText = combinedOriginalText;
                    sel.IsTranslated = true;

                    // Propagate inferred font size from blocks
                    if (blocks.AsValueEnumerable().Any())
                    {
                        sel.InferredFontSize = blocks[0].InferredFontSize;
                    }
                    else if (isFailed)
                    {
                        sel.InferredFontSize = 14.0;
                    }

                    if (sel.IsTranslated)
                    {
                        sel.EstimatedTextHeight = EstimateTranslatedTextHeight(sel);
                    }

                    // V8: 翻譯後重新整理遮罩和 Win32 Region
                    // 因為 IsTranslated 不在 WhenAnyValue 訂閱中，必須手動觸發
                    UpdateMask();
                });
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[TranslationMode] TranslateAll cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TranslationMode] TranslateAll error: {ex}");
        }
        finally
        {
            IsTranslating = false;
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[TranslationMode] Unexpected outer error: {ex}");
    }
}

    /// <summary>
    /// 翻譯模式：PaddleOCR 掃描全螢幕偵測可翻譯文字區域
    /// </summary>
    private async Task ScanAllTextAsync()
    {
        System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllText triggered");

        ShowTopLoadingBar = true;
        IsIndeterminate = true;

        try
        {
            if (!await EnsureTranslationEngineReadyAsync()) return;

            if (_mainVm?.AIResourceService == null || _translationSession == null)
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllText: translation dependencies are NULL");
                return;
            }

            // Ensure OCR resources
            bool ready = await _translationSession.EnsureOcrReadyAsync(_mainVm.SourceLanguage);
            if (!ready)
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] OCR not ready for scan");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllText: Capturing full screen...");
            // 擷取全螢幕
            var fullScreenRect = new Rect(0, 0, ViewportSize.Width, ViewportSize.Height);
            using var bitmap = await _captureService.CaptureScreenAsync(fullScreenRect, ScreenOffset, VisualScaling, false);
            if (bitmap == null) 
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllText: FAILED to capture screen");
                return;
            }
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] ScanAllText: Captured bitmap {bitmap.Width}x{bitmap.Height}");

            // 使用 PaddleOCR 偵測文字區域
            using var ocrEngine = new PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.OcrRuntimeService);
            var ocrLang = _mainVm.AppSettingsService.Settings.SourceLanguage;
            await ocrEngine.EnsureLoadedAsync(ocrLang);
            
            var textBoxes = await Task.Run(() => ocrEngine.DetectText(bitmap));

            System.Diagnostics.Debug.WriteLine($"[TranslationMode] Found {textBoxes.Count} text regions");

            // 將偵測到的文字區域轉換為 UserSelectionRect
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UserSelections.Clear();

                // 計算工具列區域（用於排除）
                double tbLeft = TranslationToolbarLeft >= 0 ? TranslationToolbarLeft : (ViewportSize.Width - (ToolbarWidth > 0 ? ToolbarWidth : 200)) / 2;
                double tbTop = TranslationToolbarTop;
                double tbWidth = ToolbarWidth > 0 ? ToolbarWidth + 20 : 220;
                double tbHeight = ToolbarHeight > 0 ? ToolbarHeight + 10 : 55;
                var toolbarRect = new Rect(tbLeft, tbTop, tbWidth, tbHeight);

                foreach (var box in textBoxes)
                {
                    // 座標從 bitmap 空間轉換回螢幕邏輯座標
                    double scaleX = ViewportSize.Width / bitmap.Width;
                    double scaleY = ViewportSize.Height / bitmap.Height;

                    var bounds = new Rect(
                        box.Left * scaleX,
                        box.Top * scaleY,
                        box.Width * scaleX,
                        box.Height * scaleY
                    );

                    // 過濾太小的區域 + 排除工具列範圍
                    if (bounds.Width > 10 && bounds.Height > 5 && !bounds.Intersects(toolbarRect))
                    {
                        UserSelections.Add(new UserSelectionRect { Bounds = bounds });
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[TranslationMode] Added {UserSelections.Count} valid selections (excluded toolbar area)");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] ScanAll error: {ex}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
        }
    }

    /// <summary>
    /// 估算翻譯文字在特定寬度下的高度，作為 HitTest Win32 Region 大小參考
    /// </summary>
    private async Task ScanAllDetectedTranslationParagraphsAsync()
    {
        System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllDetectedTranslationParagraphs triggered");

        ShowTopLoadingBar = true;
        IsIndeterminate = true;

        try
        {
            await EnsureTranslationOcrSearchCandidatesAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                MaterializeAllTranslationOcrCandidates();
                System.Diagnostics.Debug.WriteLine($"[TranslationMode] Materialized {_translationOcrParagraphCandidates.Count} paragraph candidates");
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] ScanAllDetectedTranslationParagraphs error: {ex}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
        }
    }

    private double EstimateTranslatedTextHeight(UserSelectionRect sel)
    {
        if (string.IsNullOrWhiteSpace(sel.TranslatedText)) return 0;

        var text = sel.TranslatedText;
        double fontSize = sel.InferredFontSize; // Use inferred font size from OCR
        double lineHeight = fontSize * 1.8;
        double padding = 28; // Border padding + extra
        double currentWidth = sel.Bounds.Width;
        double usableWidth = Math.Max(currentWidth - padding, 40);

        double EstimateTextWidth(string s)
        {
            double w = 0;
            foreach (char c in s)
            {
                if (c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || 
                    c >= 0xFF00 && c <= 0xFFEF || c >= 0x3000 && c <= 0x303F)
                    w += fontSize * 1.1; 
                else
                    w += fontSize * 0.6; 
            }
            return w;
        }

        int totalLines = CountWrappedLines(text, usableWidth, EstimateTextWidth, out _);
        return totalLines * lineHeight + padding;
    }

    /// <summary>
    /// V8: 根據翻譯文字長度自動撐開選取範圍
    /// </summary>
    private void AutoFitSelectionToText(UserSelectionRect sel)
    {
        if (string.IsNullOrWhiteSpace(sel.TranslatedText)) return;

        var text = sel.TranslatedText;
        double fontSize = sel.InferredFontSize; // Use inferred font size
        double lineHeight = fontSize * 1.8; // 行高
        double padding = 28; // Border padding + margin + extra

        // 估算文字寬度（考慮 CJK 全形字元）
        double EstimateTextWidth(string s)
        {
            double w = 0;
            foreach (char c in s)
            {
                if (c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || 
                    c >= 0xFF00 && c <= 0xFFEF || c >= 0x3000 && c <= 0x303F)
                    w += fontSize * 1.1; // CJK 全形
                else
                    w += fontSize * 0.6; // Latin 半形
            }
            return w;
        }

        double currentWidth = sel.Bounds.Width;
        double usableWidth = Math.Max(currentWidth - padding, 40);

        // 計算需要的行數
        int totalLines = CountWrappedLines(text, usableWidth, EstimateTextWidth, out int explicitLineCount);

        double requiredHeight = totalLines * lineHeight + padding;
        double requiredWidth = currentWidth;

        // 如果文字很短（單行），確保寬度足以容納
        if (explicitLineCount == 1)
        {
            double singleLineWidth = EstimateTextWidth(text) + padding;
            if (singleLineWidth > currentWidth)
            {
                requiredWidth = Math.Min(singleLineWidth, 500);
            }
        }

        // 只擴展，不縮小
        double newWidth = Math.Max(currentWidth, requiredWidth);
        double newHeight = Math.Max(sel.Bounds.Height, requiredHeight);

        // 最小尺寸
        newWidth = Math.Max(newWidth, 80);
        newHeight = Math.Max(newHeight, 50);

        if (Math.Abs(newWidth - sel.Bounds.Width) > 1 || Math.Abs(newHeight - sel.Bounds.Height) > 1)
        {
            sel.Bounds = new Rect(sel.Bounds.X, sel.Bounds.Y, newWidth, newHeight);
            System.Diagnostics.Debug.WriteLine($"[AutoFit] Expanded to {newWidth:F0}x{newHeight:F0} for {totalLines} lines, text='{text}'");
        }
    }

    private async Task<bool> EnsureTranslationEngineReadyAsync()
    {
        try
        {
            if (_mainVm == null || _translationSession == null)
            {
                return false;
            }

            var result = await _translationSession.CheckEngineReadyAsync(
                _mainVm.SourceLanguage,
                _mainVm.TargetLanguage);
            if (result.IsReady) return true;

            if (result.ErrorKey != null)
            {
                var title = LocalizationService.Instance["StatusError"];
                var message = LocalizationService.Instance[result.ErrorKey];
                await _mainVm.ConfirmAction!(title, message, true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] EnsureEngineReady error: {ex}");
        }

        return false;
    }

    /// <summary>
    /// 釘選翻譯結果（支援可選取文字）
    /// </summary>
    private async Task PinTranslationAsync(object? item)
    {
        if (item == null) return;

        // 隱藏視窗以便擷取乾淨的螢幕畫面
        HideAction?.Invoke();
        await Task.Delay(200); // 等待視窗完全隱藏

        try
        {
            await PinTranslationItemCoreAsync(item);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] PinTranslation error: {ex}");
        }
        finally
        {
            // 釘選後關閉擷取工作
            CloseAction?.Invoke();
        }
    }

    internal async Task PinPersistentTranslationItemAsync(TranslationResultItem item)
    {
        await PinTranslationItemCoreAsync(item);
    }

    internal System.Collections.Generic.IReadOnlyList<TranslationResultItem> MaterializePersistentTranslationSelections()
    {
        var persistentItems = new System.Collections.Generic.List<TranslationResultItem>();
        for (int i = UserSelections.Count - 1; i >= 0; i--)
        {
            var selection = UserSelections[i];
            if (!selection.IsTranslated || string.IsNullOrWhiteSpace(selection.TranslatedText))
            {
                continue;
            }

            persistentItems.Add(TranslationResultItem.FromSelection(selection));
            UserSelections.RemoveAt(i);
        }

        persistentItems.Reverse();
        return persistentItems;
    }

    private async Task PinTranslationItemCoreAsync(object item)
    {
        if (!TryResolveTranslationPinPayload(item, out var bounds, out var text, out var fontSize) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var skBitmap = await _captureService.CaptureScreenAsync(bounds, ScreenOffset, VisualScaling, false);
        if (skBitmap == null)
        {
            return;
        }

        if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(skBitmap, out var avaloniaBitmap, out var conversionError)
            || avaloniaBitmap == null)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] PinTranslation conversion failed: {conversionError}");
            return;
        }

        OpenPinWindowAction?.Invoke(avaloniaBitmap, bounds, SelectionBorderColor, SelectionBorderThickness, false, false, text, fontSize);
    }

    private static bool TryResolveTranslationPinPayload(object item, out Rect bounds, out string? text, out double fontSize)
    {
        bounds = default;
        text = null;
        fontSize = 12.0;

        switch (item)
        {
            case UserSelectionRect sel:
                bounds = sel.Bounds;
                text = sel.TranslatedText;
                fontSize = sel.InferredFontSize;
                return true;
            case TranslationResultItem result:
                bounds = result.Bounds;
                text = result.TranslatedText;
                fontSize = result.InferredFontSize;
                return true;
            case TranslatedBlock block:
                bounds = block.Bounds;
                text = block.TranslatedText;
                fontSize = block.InferredFontSize;
                return true;
            default:
                return false;
        }
    }

    private async Task PinAllTranslationsAsync()
    {
        try
        {
            var translatedSelections = UserSelections
                .AsValueEnumerable()
                .Where(sel => sel.Bounds.Width > 0
                    && sel.Bounds.Height > 0
                    && !string.IsNullOrWhiteSpace(sel.TranslatedText))
                .ToList();

            var translatedBlocks = TranslatedBlocks
                .AsValueEnumerable()
                .Where(block => block.Bounds.Width > 0
                    && block.Bounds.Height > 0
                    && !string.IsNullOrWhiteSpace(block.TranslatedText))
                .ToList();

            if (translatedSelections.Count == 0 && translatedBlocks.Count == 0)
            {
                return;
            }

            Rect captureBounds = default;
            bool hasBounds = false;

            void Include(Rect bounds)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                captureBounds = hasBounds ? captureBounds.Union(bounds) : bounds;
                hasBounds = true;
            }

            foreach (var selection in translatedSelections)
            {
                Include(selection.Bounds);
            }

            foreach (var block in translatedBlocks)
            {
                Include(block.Bounds);
            }

            if (!hasBounds) return;

            HideAction?.Invoke();
            await Task.Delay(200);

            try
            {
                using var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(
                    captureBounds,
                    ScreenOffset,
                    VisualScaling,
                    Annotations,
                    translatedSelections,
                    translatedBlocks,
                    _mainVm?.ShowSnipCursor ?? false);

                var avaloniaBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                    new Avalonia.PixelSize(skBitmap.Width, skBitmap.Height),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                using var lockedOut = avaloniaBitmap.Lock();
                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)skBitmap.GetPixels(),
                        (void*)lockedOut.Address,
                        lockedOut.RowBytes * lockedOut.Size.Height,
                        skBitmap.RowBytes * skBitmap.Height);
                }

                OpenPinWindowAction?.Invoke(
                    avaloniaBitmap,
                    captureBounds,
                    SelectionBorderColor,
                    SelectionBorderThickness,
                    false,
                    false,
                    null,
                    12.0);
            }
            finally
            {
                CloseAction?.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] PinAllTranslations error: {ex}");
        }
    }

    private static string BuildCombinedBlockText(System.Collections.Generic.IReadOnlyList<TranslatedBlock> blocks, Func<TranslatedBlock, string?> selector)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            string? value = selector(blocks[i]);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(value);
        }

        return builder.ToString();
    }

    private static int CountWrappedLines(string text, double usableWidth, Func<string, double> estimateTextWidth, out int explicitLineCount)
    {
        explicitLineCount = 0;
        int totalLines = 0;
        int lineStart = 0;

        while (lineStart <= text.Length)
        {
            int newlineIndex = text.IndexOf('\n', lineStart);
            string line;
            if (newlineIndex < 0)
            {
                line = text[lineStart..];
                lineStart = text.Length + 1;
            }
            else
            {
                line = text.Substring(lineStart, newlineIndex - lineStart);
                lineStart = newlineIndex + 1;
            }

            explicitLineCount++;
            double lineWidth = estimateTextWidth(line);
            totalLines += Math.Max(1, (int)Math.Ceiling(lineWidth / usableWidth));
        }

        return totalLines;
    }
}
