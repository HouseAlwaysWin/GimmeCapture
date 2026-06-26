using System;
using GimmeCapture.Services.Core.Media;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class CompositeGridLayoutTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(9, 3, 3)]
    public void Columns_And_Rows_FormNearSquareGrid(int count, int expectedCols, int expectedRows)
    {
        Assert.Equal(expectedCols, CompositeGridLayout.Columns(count));
        Assert.Equal(expectedRows, CompositeGridLayout.Rows(count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(9)]
    public void ComputeCells_ReturnsOneCellPerWindow_WithinCanvas(int count)
    {
        var cells = CompositeGridLayout.ComputeCells(count, 1920, 1080);

        Assert.Equal(count, cells.Length);
        foreach (var cell in cells)
        {
            Assert.True(cell.Left >= -0.01f && cell.Top >= -0.01f);
            Assert.True(cell.Right <= 1920 + 0.01f && cell.Bottom <= 1080 + 0.01f);
            Assert.True(cell.Width > 0 && cell.Height > 0);
        }
    }

    [Fact]
    public void ComputeCells_CellsDoNotOverlap()
    {
        var cells = CompositeGridLayout.ComputeCells(4, 1000, 1000);
        for (int i = 0; i < cells.Length; i++)
        {
            for (int j = i + 1; j < cells.Length; j++)
            {
                bool disjoint = cells[i].Right <= cells[j].Left + 0.01f
                    || cells[j].Right <= cells[i].Left + 0.01f
                    || cells[i].Bottom <= cells[j].Top + 0.01f
                    || cells[j].Bottom <= cells[i].Top + 0.01f;
                Assert.True(disjoint, $"cell {i} overlaps cell {j}");
            }
        }
    }

    [Fact]
    public void CanvasSize_IsEven_AndCappedToMaxEdge()
    {
        var (w, h) = CompositeGridLayout.CanvasSize([(1920, 1020), (1920, 1020)], maxEdge: 2560);

        Assert.True(w % 2 == 0 && h % 2 == 0);
        Assert.True(Math.Max(w, h) <= 2560);
        // 2 windows side-by-side => wider than tall.
        Assert.True(w >= h);
    }

    [Fact]
    public void CanvasSize_EmptySources_ReturnsMinimalEven()
    {
        var (w, h) = CompositeGridLayout.CanvasSize([]);
        Assert.True(w >= 2 && h >= 2 && w % 2 == 0 && h % 2 == 0);
    }

    [Fact]
    public void FitInto_PreservesAspectRatio_AndCentersInsideCell()
    {
        var cell = new SKRect(0, 0, 800, 600);
        var fit = CompositeGridLayout.FitInto(1920, 1080, cell); // 16:9 into 4:3 cell

        // Stays inside the cell.
        Assert.True(fit.Left >= cell.Left - 0.01f && fit.Top >= cell.Top - 0.01f);
        Assert.True(fit.Right <= cell.Right + 0.01f && fit.Bottom <= cell.Bottom + 0.01f);

        // Aspect preserved (16:9).
        Assert.Equal(16.0 / 9.0, fit.Width / fit.Height, 3);

        // Centered: equal margins on at least one axis (here vertical letterbox).
        float topMargin = fit.Top - cell.Top;
        float bottomMargin = cell.Bottom - fit.Bottom;
        Assert.Equal(topMargin, bottomMargin, 2);
    }

    [Fact]
    public void FitInto_DegenerateInput_ReturnsCell()
    {
        var cell = new SKRect(0, 0, 100, 100);
        Assert.Equal(cell, CompositeGridLayout.FitInto(0, 0, cell));
    }
}
