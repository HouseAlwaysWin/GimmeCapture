using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

// The history grid is a virtualizing list of rows, so only the rows on screen get cards built.
public class HistoryRowTests
{
    private sealed class NoCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }

    internal static HistoryItemViewModel Card(string id)
    {
        var command = new NoCommand();
        return new HistoryItemViewModel(
            new CaptureHistoryItem { Id = id, FilePath = $"C:/captures/{id}.png" }, command, command, command, command);
    }

    private static List<HistoryItemViewModel> Cards(int count) =>
        Enumerable.Range(0, count).Select(i => Card($"item{i}")).ToList();

    [Fact]
    public void SplitsTheCardsIntoRowsOfTheColumnCount()
    {
        var cards = Cards(7);

        var rows = HistoryRow.Build(cards, columns: 3, previous: []);

        Assert.Equal(new[] { 3, 3, 1 }, rows.Select(row => row.Items.Count));
        Assert.Same(cards[6], rows[2].Items[0]);
    }

    [Fact]
    public void AnUnchangedRow_IsReused_SoItsCardsAreKept()
    {
        var cards = Cards(6);
        var first = HistoryRow.Build(cards, columns: 3, previous: []);

        var second = HistoryRow.Build(cards, columns: 3, previous: first);

        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
    }

    [Fact]
    public void ACardAddedInFront_GivesTheFirstRowNewContents()
    {
        var cards = Cards(6);
        var first = HistoryRow.Build(cards, columns: 3, previous: []);
        var withNew = new[] { Card("new") }.Concat(cards).ToList();

        var second = HistoryRow.Build(withNew, columns: 3, previous: first);

        Assert.NotSame(first[0], second[0]);
        Assert.Same(withNew[0], second[0].Items[0]);
        Assert.Equal(3, second.Count);
    }

    [Fact]
    public void AColumnCountBelowOne_StillGivesOneCardPerRow()
    {
        Assert.Equal(2, HistoryRow.Build(Cards(2), columns: 0, previous: []).Count);
    }
}
