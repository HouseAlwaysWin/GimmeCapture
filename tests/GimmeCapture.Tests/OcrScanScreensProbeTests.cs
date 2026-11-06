using System.Diagnostics;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.OCR;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What the overlay's OCR scan finds on two screens side by side (1920x1080 and 2048x1160, as on the user's desktop),
/// read as one 3968x1160 capture as the scan reads it, screen by screen, or the cursor's screen alone: how many of the
/// lines of text drawn on them get a box of their own or are at least covered by one, by text size, and how long
/// detection takes. The screens are drawn here, panels of UI text at 11-24 px in Traditional Chinese and English, so
/// every line's place is known. Opt-in: OCR_SCREENS_PROBE=1 and AI_MODELS_DIR, the AI folder of an install with the
/// Traditional Chinese OCR module (a copy of it: the runtime may save optimized models beside the ones it loads).
/// <para>
/// On the user's desktop on 2026-11-06, 226 lines: read as one capture 194 boxes, 4% of the lines with a box of their
/// own and 86% covered, detection 59-69 ms; screen by screen 226 boxes, 62% and 100%, 288-320 ms (each screen's first
/// run after loading 142-154 ms, about as long as later ones); the cursor's screen alone 68% and 100% of its 106 lines
/// in 143-156 ms. Read as <see cref="OcrScanTiles"/> reads them, parts of the one capture, the same boxes as two screens.
/// </para>
/// </summary>
[Collection(AIGlobalUnloadCollection.Name)]
public sealed class OcrScanScreensProbeTests : IDisposable
{
    private static readonly int[] Sizes = [11, 12, 13, 14, 16, 18, 20, 24];

    private static readonly string[] Words =
    [
        "檔案", "編輯", "檢視", "說明", "設定", "工具列", "開始使用", "最近開啟的檔案", "儲存變更", "取消", "確定",
        "搜尋設定", "通知與動作", "系統音效", "顯示器解析度", "網路與網際網路", "個人化", "應用程式", "帳戶",
        "時間與語言", "協助工具", "隱私權", "更新與安全性", "下載", "文件", "圖片", "音樂", "影片",
        "File", "Edit", "View", "Settings", "Recent documents", "Save changes", "Cancel", "Search", "Notifications",
        "Display resolution", "Network", "Personalization", "Apps", "Accounts", "Downloads", "Pictures", "Videos",
        "2026-11-06", "18:32", "v0.74.0", "128 MB", "3968x1160",
    ];

    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public OcrScanScreensProbeTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDir))
        {
            Directory.Delete(_baseDir, recursive: true);
        }
    }

    [OptInFact("OCR_SCREENS_PROBE=1", "AI_MODELS_DIR")]
    public async Task TwoScreens_ReadAsOne_ScreenByScreen_OrTheCursorsAlone()
    {
        var settings = new AppSettingsService(_baseDir);
        settings.Settings.AIResourcesDirectory = Environment.GetEnvironmentVariable("AI_MODELS_DIR")!;
        var aiResources = new AIResourceService(settings, new AIPathService(settings), new AIModelDownloader());
        Assert.True(aiResources.IsOCRReady(OCRLanguage.TraditionalChinese), "No Traditional Chinese OCR module.");
        using var runtime = new OcrRuntimeService(aiResources);
        using var engine = new PaddleOCREngine(aiResources, settings, runtime);
        await engine.EnsureDetectionLoadedAsync(OCRLanguage.TraditionalChinese);

        var (left, leftLines) = DrawScreen(1920, 1080, seed: 1);
        var (right, rightLines) = DrawScreen(2048, 1160, seed: 2);
        using var leftScreen = left;
        using var rightScreen = right;
        using var desktop = new SKBitmap(3968, 1160, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(desktop))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(leftScreen, 0, 0);
            canvas.DrawBitmap(rightScreen, 1920, 0);
        }

        var allLines = leftLines.Select(l => (l.Size, l.Rect))
            .Concat(rightLines.Select(l => (l.Size, Rect: Offset(l.Rect, 1920, 0))))
            .ToList();
        _output.WriteLine($"lines drawn: {leftLines.Count} on the left screen, {rightLines.Count} on the right");

        // One run at each input size first: a new size costs the runtime more the first time, and the app unloads the
        // detector two minutes after a scan, so most scans are such a first run.
        var first = Stopwatch.StartNew();
        engine.DetectText(desktop);
        double desktopFirst = first.Elapsed.TotalMilliseconds;
        first.Restart();
        engine.DetectText(leftScreen);
        double leftFirst = first.Elapsed.TotalMilliseconds;
        first.Restart();
        engine.DetectText(rightScreen);
        double rightFirst = first.Elapsed.TotalMilliseconds;
        _output.WriteLine(
            $"first runs after loading: the 3968x1160 capture {desktopFirst:0} ms, then the left screen {leftFirst:0} ms, " +
            $"the right screen {rightFirst:0} ms (the same input size as the left)");

        Report("one 3968x1160 capture (now)", allLines, () => engine.DetectText(desktop));
        Report("screen by screen", allLines, () =>
            engine.DetectText(leftScreen).Concat(engine.DetectText(rightScreen).Select(b => OffsetI(b, 1920, 0))).ToList());
        Report("the cursor's screen alone (left)", leftLines.Select(l => (l.Size, l.Rect)).ToList(), () => engine.DetectText(leftScreen));

        // As the scan now reads them: parts of the one capture, worked out from the screens' bounds.
        var tiles = OcrScanTiles.ForScreens(
            [new Avalonia.Rect(0, 0, 1920, 1080), new Avalonia.Rect(1920, 0, 2048, 1160)], new Avalonia.Rect(0, 0, 3968, 1160), 3968, 1160);
        Report("as the scan reads them (OcrScanTiles)", allLines, () => OcrScanTiles.Detect(engine, desktop, tiles, CancellationToken.None));
    }

    private void Report(string name, List<(int Size, SKRect Rect)> lines, Func<List<SKRectI>> detect)
    {
        var times = new List<double>();
        List<SKRectI> boxes = [];
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            boxes = detect();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        _output.WriteLine($"{name}: detection {times[2]:0} ms (median of 5, {times[0]:0}-{times[4]:0}), {boxes.Count} boxes");
        int ownTotal = 0, coveredTotal = 0;
        foreach (int size in Sizes)
        {
            var ofSize = lines.Where(l => l.Size == size).ToList();
            int own = ofSize.Count(l => boxes.Any(b => IntersectionOverUnion(l.Rect, b) >= 0.5));
            int covered = ofSize.Count(l => boxes.Any(b => Covered(l.Rect, b) >= 0.5));
            ownTotal += own;
            coveredTotal += covered;
            _output.WriteLine($"  {size,2} px: {ofSize.Count,3} lines, {own,3} with a box of their own, {covered,3} covered");
        }

        _output.WriteLine($"  all: {lines.Count} lines, {ownTotal} ({100.0 * ownTotal / lines.Count:0}%) own box, {coveredTotal} ({100.0 * coveredTotal / lines.Count:0}%) covered");
    }

    // Panels of text in the colours of light and dark windows, each panel in one text size.
    private static (SKBitmap Bitmap, List<(int Size, SKRect Rect)> Lines) DrawScreen(int width, int height, int seed)
    {
        var random = new Random(seed);
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var lines = new List<(int, SKRect)>();
        using var typeface = SKTypeface.FromFamilyName("Microsoft JhengHei") ?? SKTypeface.Default;
        (SKColor Back, SKColor Text)[] themes =
        [
            (SKColors.White, new SKColor(0x1F, 0x1F, 0x1F)),
            (new SKColor(0x20, 0x20, 0x20), new SKColor(0xE0, 0xE0, 0xE0)),
            (new SKColor(0xF3, 0xF3, 0xF3), new SKColor(0x33, 0x33, 0x33)),
            (new SKColor(0x1E, 0x1E, 0x2E), new SKColor(0xCD, 0xD6, 0xF4)),
        ];
        using var canvas = new SKCanvas(bitmap);
        int columns = 3, rows = 2, panel = 0;
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++, panel++)
            {
                var area = new SKRect(
                    column * width / columns, row * height / rows, (column + 1) * width / columns, (row + 1) * height / rows);
                var theme = themes[(panel + seed) % themes.Length];
                using var back = new SKPaint { Color = theme.Back };
                canvas.DrawRect(area, back);
                int size = Sizes[(panel * 3 + seed) % Sizes.Length];
                using var font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };
                using var ink = new SKPaint { Color = theme.Text, IsAntialias = true };
                float lineHeight = MathF.Ceiling(size * 1.9f);
                for (float baseline = area.Top + 16 + size; baseline + lineHeight < area.Bottom; baseline += lineHeight)
                {
                    string text = string.Join(" ", Enumerable.Range(0, random.Next(1, 6)).Select(_ => Words[random.Next(Words.Length)]));
                    float x = area.Left + 16;
                    font.MeasureText(text, out var bounds, ink);
                    if (x + bounds.Right > area.Right - 8)
                    {
                        continue;
                    }

                    canvas.DrawText(text, x, baseline, SKTextAlign.Left, font, ink);
                    lines.Add((size, new SKRect(x + bounds.Left, baseline + bounds.Top, x + bounds.Right, baseline + bounds.Bottom)));
                }
            }
        }

        return (bitmap, lines);
    }

    private static SKRect Offset(SKRect rect, float x, float y) => new(rect.Left + x, rect.Top + y, rect.Right + x, rect.Bottom + y);

    private static SKRectI OffsetI(SKRectI rect, int x, int y) => new(rect.Left + x, rect.Top + y, rect.Right + x, rect.Bottom + y);

    private static double Intersection(SKRect line, SKRectI box)
    {
        float w = Math.Min(line.Right, box.Right) - Math.Max(line.Left, box.Left);
        float h = Math.Min(line.Bottom, box.Bottom) - Math.Max(line.Top, box.Top);
        return w > 0 && h > 0 ? w * h : 0;
    }

    private static double Covered(SKRect line, SKRectI box) => Intersection(line, box) / (line.Width * line.Height);

    private static double IntersectionOverUnion(SKRect line, SKRectI box)
    {
        double inter = Intersection(line, box);
        return inter / (line.Width * line.Height + (double)box.Width * box.Height - inter);
    }
}
