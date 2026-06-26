using System;
using System.Collections.Generic;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Pure geometry for the multi-window composite recorder: picks a grid shape for N windows, sizes the
/// output canvas, divides it into uniform cells, and aspect-fits each window into its cell. No UI-thread
/// or native dependencies, so it is unit-tested directly.
/// </summary>
public static class CompositeGridLayout
{
    /// <summary>Columns for an N-cell near-square grid (rows = ceil(N/cols)).</summary>
    public static int Columns(int count)
    {
        if (count <= 1)
        {
            return 1;
        }

        return (int)Math.Ceiling(Math.Sqrt(count));
    }

    /// <summary>Rows for an N-cell grid given <see cref="Columns"/>.</summary>
    public static int Rows(int count)
    {
        int cols = Columns(count);
        return cols <= 0 ? 1 : (int)Math.Ceiling(count / (double)cols);
    }

    /// <summary>
    /// Output canvas size for the grid: uniform cells sized to the largest source, capped so the longer
    /// edge does not exceed <paramref name="maxEdge"/>, with even width/height (yuv420p/NV12 requirement).
    /// </summary>
    public static (int Width, int Height) CanvasSize(IReadOnlyList<(int Width, int Height)> sources, int maxEdge = 2560)
    {
        int n = sources?.Count ?? 0;
        if (n == 0)
        {
            return (2, 2);
        }

        int cols = Columns(n);
        int rows = Rows(n);

        int cellW = 0;
        int cellH = 0;
        foreach (var s in sources!)
        {
            cellW = Math.Max(cellW, Math.Max(s.Width, 1));
            cellH = Math.Max(cellH, Math.Max(s.Height, 1));
        }

        double canvasW = (double)cols * cellW;
        double canvasH = (double)rows * cellH;

        double longest = Math.Max(canvasW, canvasH);
        if (longest > maxEdge)
        {
            double scale = maxEdge / longest;
            canvasW *= scale;
            canvasH *= scale;
        }

        return (MakeEven((int)Math.Round(canvasW)), MakeEven((int)Math.Round(canvasH)));
    }

    /// <summary>Divides the canvas into <paramref name="count"/> uniform left-to-right, top-to-bottom cells.</summary>
    public static SKRect[] ComputeCells(int count, int canvasWidth, int canvasHeight)
    {
        if (count <= 0)
        {
            return [];
        }

        int cols = Columns(count);
        int rows = Rows(count);
        float cellW = canvasWidth / (float)cols;
        float cellH = canvasHeight / (float)rows;

        var cells = new SKRect[count];
        for (int i = 0; i < count; i++)
        {
            int row = i / cols;
            int col = i % cols;
            float left = col * cellW;
            float top = row * cellH;
            cells[i] = new SKRect(left, top, left + cellW, top + cellH);
        }

        return cells;
    }

    /// <summary>Aspect-fits a source of <paramref name="srcWidth"/>×<paramref name="srcHeight"/> centered inside <paramref name="cell"/>.</summary>
    public static SKRect FitInto(int srcWidth, int srcHeight, SKRect cell)
    {
        if (srcWidth <= 0 || srcHeight <= 0 || cell.Width <= 0 || cell.Height <= 0)
        {
            return cell;
        }

        float scale = Math.Min(cell.Width / srcWidth, cell.Height / srcHeight);
        float w = srcWidth * scale;
        float h = srcHeight * scale;
        float left = cell.Left + (cell.Width - w) / 2f;
        float top = cell.Top + (cell.Height - h) / 2f;
        return new SKRect(left, top, left + w, top + h);
    }

    private static int MakeEven(int value)
    {
        int v = Math.Max(2, value);
        return v % 2 == 0 ? v : v - 1;
    }
}
