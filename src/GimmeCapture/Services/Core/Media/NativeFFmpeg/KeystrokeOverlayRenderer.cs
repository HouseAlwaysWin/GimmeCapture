using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Draws recently pressed keys (formatted as chords like <c>Ctrl+Shift+P</c>) as fading pills along the
/// bottom-center of each recorded frame. Like <see cref="CursorOverlayRenderer"/> it polls global key
/// state once per frame (no keyboard hook), detecting up→down edges itself, so it is created once per
/// recording session and called per frame from the live encode loop.
/// </summary>
internal sealed class KeystrokeOverlayRenderer : IDisposable
{
    private const int KeystrokeDurationFrames = 48; // ~1.6s at 30fps before a pill disappears
    private const int FadeFrames = 12;              // fade out over the last N frames
    private const int MaxVisible = 4;

    private sealed class Entry
    {
        public string Text = string.Empty;
        public int Frames;
    }

    private readonly List<Entry> _recent = new();
    private readonly Dictionary<int, bool> _prevDown = new();

    // Cached across frames (created once, colours mutated per pill) instead of allocating a font + three
    // SKPaints on every recorded frame. The font size only depends on frame height (constant per session).
    private SKFont? _font;
    private float _fontSize = -1f;
    private readonly SKPaint _bgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _borderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    public void Dispose()
    {
        _font?.Dispose();
        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _textPaint.Dispose();
    }

    public void Draw(SKBitmap bmp)
    {
        AdvanceState();

        if (_recent.Count == 0)
        {
            return;
        }

        float fontSize = Math.Clamp(bmp.Height * 0.035f, 16f, 40f);
        if (_font == null || fontSize != _fontSize)
        {
            _font?.Dispose();
            _font = new SKFont(SKTypeface.Default, fontSize);
            _fontSize = fontSize;
        }
        SKFont font = _font;
        float padX = fontSize * 0.5f;
        float padY = fontSize * 0.32f;
        float gap = fontSize * 0.4f;
        float pillH = fontSize + padY * 2f;

        // Measure all pills so we can center the row.
        var widths = new float[_recent.Count];
        float total = 0f;
        for (int i = 0; i < _recent.Count; i++)
        {
            widths[i] = font.MeasureText(_recent[i].Text) + padX * 2f;
            total += widths[i];
        }
        total += gap * (_recent.Count - 1);

        float x = (bmp.Width - total) / 2f;
        float y = bmp.Height - pillH - bmp.Height * 0.06f; // a little above the bottom edge

        using var canvas = new SKCanvas(bmp);
        for (int i = 0; i < _recent.Count; i++)
        {
            float a = Math.Clamp(_recent[i].Frames / (float)FadeFrames, 0f, 1f);
            byte bgA = (byte)(190 * a);
            byte fgA = (byte)(255 * a);

            _bgPaint.Color = new SKColor(20, 20, 20, bgA);
            _borderPaint.Color = new SKColor(255, 255, 255, (byte)(70 * a));
            _textPaint.Color = new SKColor(255, 255, 255, fgA);

            var rect = new SKRect(x, y, x + widths[i], y + pillH);
            float radius = pillH * 0.28f;
            canvas.DrawRoundRect(rect, radius, radius, _bgPaint);
            canvas.DrawRoundRect(rect, radius, radius, _borderPaint);

            float baseline = y + padY + fontSize * 0.8f;
            canvas.DrawText(_recent[i].Text, x + padX, baseline, font, _textPaint);

            x += widths[i] + gap;
        }
    }

    private void AdvanceState()
    {
        bool ctrl = Down(0x11);
        bool shift = Down(0x10);
        bool alt = Down(0x12);
        bool win = Down(0x5B) || Down(0x5C);

        foreach (var (vk, name) in TrackedKeys)
        {
            bool down = Down(vk);
            bool prev = _prevDown.TryGetValue(vk, out var p) && p;
            _prevDown[vk] = down;

            if (down && !prev)
            {
                _recent.Add(new Entry { Text = BuildChord(ctrl, alt, shift, win, name), Frames = KeystrokeDurationFrames });
                if (_recent.Count > MaxVisible)
                {
                    _recent.RemoveAt(0);
                }
            }
        }

        for (int i = _recent.Count - 1; i >= 0; i--)
        {
            if (--_recent[i].Frames <= 0)
            {
                _recent.RemoveAt(i);
            }
        }
    }

    internal static string BuildChord(bool ctrl, bool alt, bool shift, bool win, string keyName)
    {
        var sb = new StringBuilder();
        if (ctrl) sb.Append("Ctrl+");
        if (alt) sb.Append("Alt+");
        if (shift) sb.Append("Shift+");
        if (win) sb.Append("Win+");
        sb.Append(keyName);
        return sb.ToString();
    }

    // ── Win32 key state ──
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool Down(int vk)
    {
        try
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    // Non-modifier keys whose press starts a chord. Modifiers (Ctrl/Alt/Shift/Win) are read separately and
    // prefixed, so they never appear on their own.
    private static readonly (int Vk, string Name)[] TrackedKeys = BuildTrackedKeys();

    private static (int, string)[] BuildTrackedKeys()
    {
        var list = new List<(int, string)>();
        for (int vk = 0x41; vk <= 0x5A; vk++) list.Add((vk, ((char)vk).ToString())); // A-Z
        for (int vk = 0x30; vk <= 0x39; vk++) list.Add((vk, ((char)vk).ToString())); // 0-9
        for (int i = 1; i <= 12; i++) list.Add((0x70 + (i - 1), "F" + i));            // F1-F12
        list.Add((0x20, "Space"));
        list.Add((0x0D, "Enter"));
        list.Add((0x09, "Tab"));
        list.Add((0x1B, "Esc"));
        list.Add((0x08, "Back"));
        list.Add((0x2E, "Del"));
        list.Add((0x2D, "Ins"));
        list.Add((0x24, "Home"));
        list.Add((0x23, "End"));
        list.Add((0x21, "PgUp"));
        list.Add((0x22, "PgDn"));
        list.Add((0x25, "←"));
        list.Add((0x26, "↑"));
        list.Add((0x27, "→"));
        list.Add((0x28, "↓"));
        return list.ToArray();
    }
}
