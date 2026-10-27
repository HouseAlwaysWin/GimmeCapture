using System.Collections.Generic;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

// The history panel rebuilt every card, and decoded every thumbnail again, after each capture. A refresh now keeps
// the view model of every entry it already shows.
public class HistoryItemsMergeTests
{
    private static List<CaptureHistoryItem> Entries(params string[] ids) =>
        ids.Select(id => new CaptureHistoryItem { Id = id, FilePath = $"C:/captures/{id}.png" }).ToList();

    private static HistoryItemViewModel Create(CaptureHistoryItem entry) => HistoryRowTests.Card(entry.Id);

    [Fact]
    public void AnEntryAlreadyShown_KeepsItsViewModel()
    {
        var (first, created, removed) = MainWindowViewModel.MergeHistoryItems([], Entries("a", "b"), Create);
        Assert.Equal(2, created.Count);
        Assert.Empty(removed);

        var (second, createdNow, removedNow) = MainWindowViewModel.MergeHistoryItems(first, Entries("new", "a", "b"), Create);

        Assert.Equal(new[] { "new", "a", "b" }, second.Select(item => item.Id));
        Assert.Same(first[0], second[1]);
        Assert.Same(first[1], second[2]);
        Assert.Equal("new", Assert.Single(createdNow).Id);
        Assert.Empty(removedNow);
    }

    [Fact]
    public void AnEntryNoLongerListed_IsHandedBackToBeDisposed()
    {
        var (first, _, _) = MainWindowViewModel.MergeHistoryItems([], Entries("a", "b", "c"), Create);

        var (second, created, removed) = MainWindowViewModel.MergeHistoryItems(first, Entries("a", "c"), Create);

        Assert.Equal(new[] { first[0], first[2] }, second);
        Assert.Empty(created);
        Assert.Same(first[1], Assert.Single(removed));
    }

    // A pinned recording saved from its pin keeps its entry, which now points at the saved file. The card showing the
    // temp file it was would open and reveal a file that is about to be deleted.
    [Fact]
    public void AnEntryWhoseFileMoved_GetsANewViewModel()
    {
        var (first, _, _) = MainWindowViewModel.MergeHistoryItems([], Entries("a", "b"), Create);
        var entries = Entries("a", "b");
        entries[1].FilePath = "D:/Videos/saved.mp4";

        var (second, created, removed) = MainWindowViewModel.MergeHistoryItems(first, entries, Create);

        Assert.Same(first[0], second[0]);
        Assert.Same(Assert.Single(created), second[1]);
        Assert.Equal("b", second[1].Id);
        Assert.Same(first[1], Assert.Single(removed));
    }
}
