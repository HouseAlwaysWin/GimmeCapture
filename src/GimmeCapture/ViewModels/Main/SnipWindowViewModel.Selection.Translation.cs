using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.OCR;
using System;
using System.Linq;
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
        System.Diagnostics.Debug.WriteLine("[TranslationMode] TranslateAllSelectionsAsync triggered");
        if (UserSelections.Count == 0)
        {
            // UX rule: translate button should only process explicit user selections.
            // If no selection exists, do nothing instead of scanning full screen.
            System.Diagnostics.Debug.WriteLine("[TranslationMode] No selections found. Skip translation.");
            _mainVm?.SetStatus("StatusTranslateNoSelection");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[TranslationMode] Proceeding to translate {UserSelections.Count} regions");
        ShowTopLoadingBar = true;
        IsIndeterminate = true;
        ProcessingText = "Translating...";

        if (_translationService == null)
        {
            if (_mainVm?.AIResourceService == null) return;
            _translationService = new TranslationService(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.MarianMTService);
        }

        // Sync language settings
        if (_mainVm != null)
        {
            _mainVm.AppSettingsService.Settings.TargetLanguage = _mainVm.TargetLanguage;
            _mainVm.AppSettingsService.Settings.SourceLanguage = _mainVm.SourceLanguage;
        }

        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = new CancellationTokenSource();
        var token = _translationCts.Token;

        // Ensure OCR resources
        if (_mainVm != null)
        {
            bool ready = await _mainVm.AIResourceService.EnsureOCRAsync();
            if (!ready)
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] OCR not ready");
                ShowTopLoadingBar = false;
                IsIndeterminate = false;
                return;
            }
        }

        try
        {
            // 逐一翻譯每個選取區域
            var selectionsCopy = UserSelections.ToList();
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
                var blocks = await Task.Run(() => _translationService.AnalyzeAndTranslateAsync(bitmap, VisualScaling, token), token);
                
                if (token.IsCancellationRequested) break;

                // 合併所有翻譯結果作為這個區域的翻譯文字
                var combinedText = string.Join("\n", blocks.Select(b => b.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)));
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    sel.TranslatedText = combinedText;
                    sel.IsTranslated = !string.IsNullOrWhiteSpace(combinedText);

                    // Propagate inferred font size from blocks
                    if (blocks.Any())
                    {
                        sel.InferredFontSize = blocks[0].InferredFontSize;
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
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
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

        if (_mainVm?.AIResourceService == null) 
        {
            System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllText: mainVm or AIResourceService is NULL");
            return;
        }

        // Ensure OCR resources
        bool ready = await _mainVm.AIResourceService.EnsureOCRAsync();
        if (!ready)
        {
            System.Diagnostics.Debug.WriteLine("[TranslationMode] OCR not ready for scan");
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
            return;
        }

        try
        {
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
            var ocrEngine = new PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService);
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

            ocrEngine.Dispose();
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

        var lines = text.Split('\n');
        int totalLines = 0;
        foreach (var line in lines)
        {
            double lineWidth = EstimateTextWidth(line);
            int wrappedLines = Math.Max(1, (int)Math.Ceiling(lineWidth / usableWidth));
            totalLines += wrappedLines;
        }

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
        var lines = text.Split('\n');
        int totalLines = 0;
        foreach (var line in lines)
        {
            double lineWidth = EstimateTextWidth(line);
            int wrappedLines = Math.Max(1, (int)Math.Ceiling(lineWidth / usableWidth));
            totalLines += wrappedLines;
        }

        double requiredHeight = totalLines * lineHeight + padding;
        double requiredWidth = currentWidth;

        // 如果文字很短（單行），確保寬度足以容納
        if (lines.Length == 1)
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
}
