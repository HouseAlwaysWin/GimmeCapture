using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;

namespace GimmeCapture.Tests;

/// <summary>
/// A stand-in for a manual scrolling capture: a tall page drawn like a chat or a document, a scroll path (the view's
/// top row on the page, frame by frame), and the frames the capture would grab, with what an app keeps fixed while
/// its content scrolls (a header bar, a message box) drawn over them. Because each frame's place on the page is
/// known, what a stitcher keeps can be traced back to page rows: a row kept twice is the duplication users see.
/// </summary>
internal static class ScrollCaptureSimulation
{
    internal sealed class Page : IDisposable
    {
        public Page(SKBitmap bitmap, List<(int Top, int Bottom)> blocks)
        {
            Bitmap = bitmap;
            Blocks = blocks;
        }

        public SKBitmap Bitmap { get; }

        /// <summary>Each message's rows, which a hover highlights as one.</summary>
        public List<(int Top, int Bottom)> Blocks { get; }

        public int Width => Bitmap.Width;

        public int Height => Bitmap.Height;

        public void Dispose() => Bitmap.Dispose();
    }

    /// <summary>What an app draws at a fixed place over its scrolling content.</summary>
    /// <param name="FooterFromFrame">The message box shows only from this frame on (a bar that appears mid-capture).</param>
    /// <param name="DatePill">A pill floating at the top in the middle, as LINE shows the date while it scrolls.</param>
    /// <param name="AnimatedTop">Page rows from here, <paramref name="AnimatedRows"/> of them, change every frame (a GIF).</param>
    internal sealed record Overlays(
        int HeaderRows = 0,
        int FooterRows = 0,
        int HoverRow = -1,
        int Jitter = 0,
        int FooterFromFrame = 0,
        bool DatePill = false,
        int AnimatedTop = -1,
        int AnimatedRows = 0)
    {
        public bool FooterShown(int frameIndex) => FooterRows > 0 && frameIndex >= FooterFromFrame;
    }

    private static readonly SKColor DarkBackground = new(49, 51, 56);
    private static readonly SKColor DarkText = new(219, 222, 225);
    private static readonly SKColor DarkMuted = new(148, 155, 164);

    /// <summary>
    /// A chat drawn like Discord's dark theme: messages with an avatar, a name and lines of text, runs of messages
    /// from one author, date separators, images, and the short phrases and stickers chats repeat word for word.
    /// </summary>
    public static Page Chat(int width, int height, int seed, int repeatPercent = 25)
    {
        var random = new Random(seed);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var blocks = new List<(int Top, int Bottom)>();
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(DarkBackground);

        using SKBitmap sticker = Sticker(96, seed: 7);
        var authors = Enumerable.Range(0, 5).Select(i => new SKColor(
            (byte)random.Next(80, 255), (byte)random.Next(80, 255), (byte)random.Next(80, 255))).ToArray();

        int y = 16;
        int author = -1;
        while (y < height - 160)
        {
            int kind = random.Next(100);
            if (kind < 6)
            {
                // A date separator: a rule across with a label in the middle, the same shape every time.
                int label = random.Next(3);
                DrawRule(canvas, 16, y + 10, width - 32, new SKColor(63, 65, 71));
                DrawPhrase(canvas, (width / 2) - 40, y + 4, 80, DarkMuted, phrase: 100 + label, em: 11);
                y += 30;
                author = -1;
                continue;
            }

            int top = y;
            int next = random.Next(authors.Length);
            bool continuation = next == author && random.Next(3) > 0;
            author = next;
            int textLeft = 72;
            if (!continuation)
            {
                y += 6;
                using var avatar = new SKPaint { Color = authors[author], IsAntialias = true };
                canvas.DrawCircle(36, y + 20, 20, avatar);
                DrawPhrase(canvas, textLeft, y, 120, authors[author], phrase: 200 + author, em: 14);
                DrawPhrase(canvas, textLeft + 130, y + 3, 70, DarkMuted, phrase: 300 + random.Next(40), em: 10);
                y += 22;
            }

            int content = random.Next(100);
            if (content < repeatPercent)
            {
                // A short phrase chats repeat word for word ("ok", "lol", "+1").
                DrawPhrase(canvas, textLeft, y, 160, DarkText, phrase: random.Next(6), em: 14);
                y += 22;
            }
            else if (content < repeatPercent + 8)
            {
                canvas.DrawBitmap(sticker, textLeft, y + 2);
                y += sticker.Height + 6;
            }
            else if (content < repeatPercent + 16)
            {
                int imageHeight = random.Next(120, 260);
                DrawImage(canvas, random, textLeft, y + 2, Math.Min(360, width - textLeft - 40), imageHeight);
                y += imageHeight + 8;
            }
            else
            {
                int lines = random.Next(1, 5);
                for (int line = 0; line < lines; line++)
                {
                    DrawText(canvas, random, textLeft, y, width - textLeft - 60, DarkText, em: 14);
                    y += 22;
                }
            }

            y += continuation ? 2 : 10;
            blocks.Add((top, y));
        }

        return new Page(bitmap, blocks);
    }

    private static readonly SKColor LineBackground = new(34, 34, 34);

    /// <summary>
    /// A chat drawn like LINE's dark theme: mostly empty background, received bubbles on the left under an avatar,
    /// sent bubbles on the right with the time and "read" beside them, small date pills in the middle, and pictures.
    /// Far sparser than <see cref="Chat"/>: most rows are background from edge to edge.
    /// </summary>
    public static Page Line(int width, int height, int seed)
    {
        var random = new Random(seed);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var blocks = new List<(int Top, int Bottom)>();
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(LineBackground);
        using var received = new SKPaint { Color = new SKColor(58, 58, 58), IsAntialias = true };
        using var sent = new SKPaint { Color = new SKColor(108, 198, 85), IsAntialias = true };
        using var avatar = new SKPaint { Color = new SKColor(120, 160, 220), IsAntialias = true };

        int y = 20;
        while (y < height - 220)
        {
            int top = y;
            int kind = random.Next(100);
            if (kind < 8)
            {
                using var pill = new SKPaint { Color = new SKColor(52, 52, 52), IsAntialias = true };
                canvas.DrawRoundRect((width / 2) - 34, y, 68, 22, 11, 11, pill);
                DrawPhrase(canvas, (width / 2) - 22, y + 5, 44, DarkMuted, phrase: 400 + random.Next(4), em: 11);
                y += 40;
                continue;
            }

            bool mine = random.Next(2) == 0;
            int lines = random.Next(1, 4);
            int bubbleWidth = random.Next(60, Math.Max(90, (width * 3 / 5) - 40));
            int bubbleHeight = 14 + (lines * 20);
            if (random.Next(100) < 10)
            {
                // A picture instead of text.
                bubbleWidth = Math.Min(width / 2, 220);
                bubbleHeight = random.Next(140, 260);
            }

            int x;
            if (mine)
            {
                x = width - 24 - bubbleWidth;
                canvas.DrawRoundRect(x, y, bubbleWidth, bubbleHeight, 14, 14, sent);
                DrawPhrase(canvas, x - 40, y + bubbleHeight - 26, 30, DarkMuted, phrase: 500, em: 9);             // read
                DrawPhrase(canvas, x - 52, y + bubbleHeight - 12, 42, DarkMuted, phrase: 501 + random.Next(30), em: 9); // time
            }
            else
            {
                canvas.DrawCircle(30, y + 16, 16, avatar);
                x = 56;
                canvas.DrawRoundRect(x, y, bubbleWidth, bubbleHeight, 14, 14, received);
                DrawPhrase(canvas, x + bubbleWidth + 6, y + bubbleHeight - 12, 42, DarkMuted, phrase: 501 + random.Next(30), em: 9);
            }

            if (bubbleHeight > 14 + (3 * 20))
            {
                DrawImage(canvas, random, x + 4, y + 4, bubbleWidth - 8, bubbleHeight - 8);
            }
            else
            {
                for (int line = 0; line < lines; line++)
                {
                    DrawText(canvas, random, x + 12, y + 8 + (line * 20), bubbleWidth - 24, mine ? new SKColor(20, 20, 20) : DarkText, em: 13);
                }
            }

            y += bubbleHeight + random.Next(14, 40);
            blocks.Add((top, y));
        }

        return new Page(bitmap, blocks);
    }

    /// <summary>
    /// A frame of a scroll along the other axis: <paramref name="frame"/> turned a quarter clockwise, so content that
    /// moved up the frame moves left across this one. The capture turns it back before stitching.
    /// </summary>
    public static SKBitmap Sideways(SKBitmap frame)
    {
        SKBitmap turned = GimmeCapture.Services.Core.Media.ScrollStitcher.RotateCw90(frame);
        frame.Dispose();
        return turned;
    }

    /// <summary>
    /// The frame a capture grabs with the view's top at page row <paramref name="top"/>: the page's rows, then what
    /// the app keeps in place over them.
    /// </summary>
    public static SKBitmap Frame(Page page, int top, int height, Overlays overlays, int frameIndex = 0)
    {
        var frame = new SKBitmap(new SKImageInfo(page.Width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(frame))
        {
            canvas.DrawBitmap(page.Bitmap, new SKRect(0, top, page.Width, top + height), new SKRect(0, 0, page.Width, height));

            if (overlays.AnimatedRows > 0)
            {
                // Page rows that show something new in every frame, where they are on the page.
                var noise = new Random((frameIndex * 7) + 3);
                int first = Math.Max(overlays.AnimatedTop, top);
                int last = Math.Min(overlays.AnimatedTop + overlays.AnimatedRows, top + height);
                using var blob = new SKPaint();
                for (int i = 0; i < 30 && last > first; i++)
                {
                    blob.Color = new SKColor((byte)noise.Next(256), (byte)noise.Next(256), (byte)noise.Next(256));
                    canvas.DrawRect(72 + noise.Next(280), first - top + noise.Next(Math.Max(1, last - first)), 20, 12, blob);
                }
            }

            if (overlays.HoverRow >= 0)
            {
                // The message under a still pointer is lit; as the content scrolls under it, another one is.
                int pageRow = top + overlays.HoverRow;
                foreach ((int blockTop, int blockBottom) in page.Blocks)
                {
                    if (pageRow >= blockTop && pageRow < blockBottom)
                    {
                        using var lit = new SKPaint { Color = new SKColor(255, 255, 255, 10), BlendMode = SKBlendMode.SrcOver };
                        canvas.DrawRect(0, blockTop - top, page.Width, blockBottom - blockTop, lit);
                        break;
                    }
                }
            }

            if (overlays.HeaderRows > 0)
            {
                using var bar = new SKPaint { Color = new SKColor(43, 45, 49) };
                canvas.DrawRect(0, 0, page.Width, overlays.HeaderRows, bar);
                DrawPhrase(canvas, 20, (overlays.HeaderRows / 2) - 8, 160, DarkText, phrase: 900, em: 16);
                DrawRule(canvas, 0, overlays.HeaderRows - 1, page.Width, new SKColor(31, 33, 36));
            }

            if (overlays.FooterShown(frameIndex))
            {
                using var box = new SKPaint { Color = new SKColor(56, 58, 64) };
                int boxTop = height - overlays.FooterRows;
                canvas.DrawRect(0, boxTop, page.Width, overlays.FooterRows, new SKPaint { Color = DarkBackground });
                canvas.DrawRoundRect(16, boxTop + 12, page.Width - 32, overlays.FooterRows - 24, 8, 8, box);
                DrawPhrase(canvas, 60, boxTop + (overlays.FooterRows / 2) - 7, 200, DarkMuted, phrase: 901, em: 14);
            }
        }

        if (overlays.DatePill)
        {
            using var canvas = new SKCanvas(frame);
            using var pill = new SKPaint { Color = new SKColor(20, 20, 24, 200), IsAntialias = true };
            canvas.DrawRoundRect((page.Width / 2) - 60, 8, 120, 26, 13, 13, pill);
            DrawPhrase(canvas, (page.Width / 2) - 40, 14, 80, DarkText, phrase: 902, em: 12);
        }

        if (overlays.Jitter > 0)
        {
            AddJitter(frame, overlays.Jitter, seed: (top * 31) + frameIndex);
        }

        return frame;
    }

    /// <summary>
    /// Mouse-wheel scrolling with the smooth scrolling browsers and chat apps use: each notch moves the target
    /// <paramref name="notchRows"/> rows and the view eases toward it, sampled once a captured frame.
    /// </summary>
    public static List<int> WheelPath(int start, int end, int seed, double notchesPerFrame, int notchRows = 100, int maxFrames = 2000)
    {
        var random = new Random(seed);
        var tops = new List<int> { start };
        int direction = Math.Sign(end - start);
        double position = start;
        double target = start;
        while (tops.Count < maxFrames)
        {
            double notches = notchesPerFrame;
            while (notches > 0)
            {
                if (random.NextDouble() < Math.Min(1.0, notches))
                {
                    target += direction * notchRows;
                }

                notches -= 1.0;
            }

            target = direction > 0 ? Math.Min(target, end) : Math.Max(target, end);
            position += (target - position) * 0.35;
            int top = (int)Math.Round(position);
            tops.Add(top);
            if (top == end)
            {
                break;
            }
        }

        return tops;
    }

    /// <summary>A steady drag (a scrollbar thumb, a touchpad): <paramref name="rowsPerFrame"/> rows a frame.</summary>
    public static List<int> SteadyPath(int start, int end, int rowsPerFrame)
    {
        var tops = new List<int>();
        int direction = Math.Sign(end - start);
        for (int top = start; direction > 0 ? top < end : top > end; top += direction * rowsPerFrame)
        {
            tops.Add(top);
        }

        tops.Add(end);
        return tops;
    }

    /// <summary>
    /// The page rows behind what a stitcher kept, in the order it kept them: the top band (rows of the frame at the
    /// strip's top), the strip (the first frame's rows between the bands, then each frame's rows it added below or
    /// above), and the bottom band (rows of the frame at the strip's bottom). A frame row the app's header or message box
    /// covered stands for no page row (-1).
    /// </summary>
    internal sealed class RowTrace
    {
        private readonly List<int> _strip = new();
        private readonly int _frameHeight;
        private readonly Overlays _overlays;
        private int _topBand;
        private int _bottomBand;
        private (int Top, int Index, int First, int Count) _header;
        private (int Top, int Index, int First, int Count) _footer;

        public RowTrace(int frameHeight, Overlays overlays)
        {
            _frameHeight = frameHeight;
            _overlays = overlays;
        }

        public bool Started { get; private set; }

        public void Start(int seedTop, int topBand, int bottomBand)
        {
            Started = true;
            _topBand = topBand;
            _bottomBand = bottomBand;
            _header = (seedTop, 0, 0, topBand);
            _footer = (seedTop, 0, _frameHeight - bottomBand, bottomBand);
            _strip.AddRange(FrameRows(seedTop, 0, topBand, _frameHeight - topBand - bottomBand));
        }

        /// <summary>Follows one step of a ManualScrollStitcher: band rows taken back, the bands it used, the rows it added.</summary>
        public void Apply(GimmeCapture.Services.Core.Media.ManualScrollStep step, int frameTop, int frameIndex, int previousTop, int previousIndex)
        {
            if (step.TrimmedTop > 0)
            {
                _strip.RemoveRange(0, step.TrimmedTop);
                _header = (previousTop, previousIndex, 0, _topBand + step.TrimmedTop);
            }

            if (step.TrimmedBottom > 0)
            {
                _strip.RemoveRange(_strip.Count - step.TrimmedBottom, step.TrimmedBottom);
                _footer = (previousTop, previousIndex, _frameHeight - _bottomBand - step.TrimmedBottom, _bottomBand + step.TrimmedBottom);
            }

            _topBand = step.TopBand;
            _bottomBand = step.BottomBand;
            if (step.Prepended > 0)
            {
                Prepended(frameTop, step.Prepended, frameIndex);
            }
            else if (step.Appended > 0)
            {
                Appended(frameTop, step.Appended, frameIndex);
            }
        }

        public void Appended(int frameTop, int rows, int frameIndex)
        {
            int windowEnd = _frameHeight - _bottomBand;
            _strip.AddRange(FrameRows(frameTop, frameIndex, windowEnd - rows, rows));
            _footer = (frameTop, frameIndex, windowEnd, _bottomBand);
        }

        public void Prepended(int frameTop, int rows, int frameIndex)
        {
            _strip.InsertRange(0, FrameRows(frameTop, frameIndex, _topBand, rows));
            _header = (frameTop, frameIndex, 0, _topBand);
        }

        public IReadOnlyList<int> Rows =>
            FrameRows(_header.Top, _header.Index, _header.First, _header.Count)
                .Concat(_strip)
                .Concat(FrameRows(_footer.Top, _footer.Index, _footer.First, _footer.Count))
                .ToList();

        /// <summary>Page rows kept more than once (each extra copy counted).</summary>
        public int DuplicatedRows => Rows.Where(r => r >= 0).GroupBy(r => r).Sum(g => g.Count() - 1);

        /// <summary>Rows that came from the app’s fixed header or message box rather than the page.</summary>
        public int OverlayRows => Rows.Count(r => r < 0);

        /// <summary>Places where the kept rows do not run on from one page row to the next.</summary>
        public int Breaks
        {
            get
            {
                int breaks = 0;
                int previous = int.MinValue;
                foreach (int row in Rows.Where(r => r >= 0))
                {
                    if (previous != int.MinValue && row != previous + 1)
                    {
                        breaks++;
                    }

                    previous = row;
                }

                return breaks;
            }
        }

        /// <summary>Page rows some frame showed uncovered that were not kept.</summary>
        public int Missing(IReadOnlyList<int> frameTops)
        {
            var kept = new HashSet<int>(Rows);
            var shown = new HashSet<int>();
            for (int i = 0; i < frameTops.Count; i++)
            {
                int last = _overlays.FooterShown(i) ? _frameHeight - _overlays.FooterRows : _frameHeight;
                for (int r = _overlays.HeaderRows; r < last; r++)
                {
                    shown.Add(frameTops[i] + r);
                }
            }

            return shown.Count(row => !kept.Contains(row));
        }

        /// <summary>
        /// Rows of <paramref name="result"/> that stand for a page row but do not show it: something the app drew over
        /// the content (a floating pill) kept in the strip. A pixel counts as different past 24 on a channel, a row with
        /// more than two of them.
        /// </summary>
        public int DirtyRows(SKBitmap result, Page page)
        {
            IReadOnlyList<int> rows = Rows;
            int dirty = 0;
            for (int i = 0; i < rows.Count && i < result.Height; i++)
            {
                if (rows[i] < 0)
                {
                    continue;
                }

                int differing = 0;
                for (int x = 0; x < result.Width && differing <= 2; x++)
                {
                    SKColor a = result.GetPixel(x, i);
                    SKColor b = page.Bitmap.GetPixel(x, rows[i]);
                    if (Math.Abs(a.Red - b.Red) > 24 || Math.Abs(a.Green - b.Green) > 24 || Math.Abs(a.Blue - b.Blue) > 24)
                    {
                        differing++;
                    }
                }

                if (differing > 2)
                {
                    dirty++;
                }
            }

            return dirty;
        }

        private List<int> FrameRows(int frameTop, int frameIndex, int firstFrameRow, int count)
        {
            var rows = new List<int>(Math.Max(0, count));
            for (int r = firstFrameRow; r < firstFrameRow + count; r++)
            {
                bool covered = r < _overlays.HeaderRows
                    || (_overlays.FooterShown(frameIndex) && r >= _frameHeight - _overlays.FooterRows);
                rows.Add(covered ? -1 : frameTop + r);
            }

            return rows;
        }
    }

    private static void DrawRule(SKCanvas canvas, int x, int y, int width, SKColor color)
    {
        using var paint = new SKPaint { Color = color };
        canvas.DrawRect(x, y, width, 1, paint);
    }

    // A phrase is drawn from its own seed, so every time it is drawn it looks the same, as text repeated word for
    // word does.
    private static void DrawPhrase(SKCanvas canvas, int x, int y, int width, SKColor color, int phrase, int em) =>
        DrawText(canvas, new Random(phrase * 7919), x, y, width, color, em, fullWidth: false);

    // Text-like ink: words of glyphs made of short vertical and horizontal strokes, the rows of which have the edges
    // real text has.
    private static void DrawText(SKCanvas canvas, Random random, int x, int y, int maxWidth, SKColor color, int em, bool fullWidth = true)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        int end = x + (fullWidth ? random.Next(maxWidth / 3, maxWidth) : maxWidth);
        int cx = x;
        while (cx < end - em)
        {
            int glyphs = random.Next(2, 8);
            for (int g = 0; g < glyphs && cx < end - em; g++)
            {
                int glyphWidth = random.Next(em / 2, em);
                int strokes = random.Next(2, 5);
                for (int s = 0; s < strokes; s++)
                {
                    if (random.Next(2) == 0)
                    {
                        int sx = cx + random.Next(Math.Max(1, glyphWidth - 2));
                        int sy = y + random.Next(em / 2);
                        int sh = random.Next(em / 3, em);
                        canvas.DrawRect(sx, sy, 2, Math.Min(sh, y + em - sy), paint);
                    }
                    else
                    {
                        int sy = y + random.Next(em - 2);
                        canvas.DrawRect(cx, sy, glyphWidth - 1, 2, paint);
                    }
                }

                cx += glyphWidth + 1;
            }

            cx += em / 2;
        }
    }

    private static void DrawImage(SKCanvas canvas, Random random, int x, int y, int width, int height)
    {
        var top = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        var bottom = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(x, y), new SKPoint(x + (width / 3), y + height), new[] { top, bottom }, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(x, y, width, height, paint);
        using var speck = new SKPaint { Color = new SKColor(255, 255, 255, 60) };
        for (int i = 0; i < 40; i++)
        {
            canvas.DrawCircle(x + random.Next(width), y + random.Next(height), random.Next(2, 9), speck);
        }
    }

    private static SKBitmap Sticker(int size, int seed)
    {
        var random = new Random(seed);
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(DarkBackground);
        using var face = new SKPaint { Color = new SKColor(250, 200, 60), IsAntialias = true };
        canvas.DrawCircle(size / 2f, size / 2f, (size / 2f) - 4, face);
        using var ink = new SKPaint { Color = new SKColor(60, 40, 20), IsAntialias = true };
        canvas.DrawCircle(size * 0.35f, size * 0.4f, 6, ink);
        canvas.DrawCircle(size * 0.65f, size * 0.4f, 6, ink);
        canvas.DrawRect(size * 0.3f, size * 0.65f, size * 0.4f, 5, ink);
        for (int i = 0; i < 6; i++)
        {
            canvas.DrawCircle(random.Next(size), random.Next(size), 3, ink);
        }

        return bitmap;
    }

    // What re-rendering does to a frame (GPU compositing, subpixel text): every channel off by a little.
    private static void AddJitter(SKBitmap frame, int amount, int seed)
    {
        var random = new Random(seed);
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x += 3)
            {
                SKColor c = frame.GetPixel(x, y);
                int d = random.Next(-amount, amount + 1);
                frame.SetPixel(x, y, new SKColor(
                    (byte)Math.Clamp(c.Red + d, 0, 255),
                    (byte)Math.Clamp(c.Green + d, 0, 255),
                    (byte)Math.Clamp(c.Blue + d, 0, 255),
                    255));
            }
        }
    }
}
