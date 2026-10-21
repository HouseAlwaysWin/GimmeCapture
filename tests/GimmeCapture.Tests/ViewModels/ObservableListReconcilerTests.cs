using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GimmeCapture.ViewModels.Shared;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

// The history panel used to clear and refill its list on every refresh, so every card was rebuilt after each
// capture. A refresh now changes only what changed.
public class ObservableListReconcilerTests
{
    private sealed class Item(string name)
    {
        public override string ToString() => name;
    }

    private static readonly Item A = new("a");
    private static readonly Item B = new("b");
    private static readonly Item C = new("c");
    private static readonly Item D = new("d");

    private static (ObservableCollection<Item> List, List<NotifyCollectionChangedAction> Changes) Start(params Item[] items)
    {
        var list = new ObservableCollection<Item>(items);
        var changes = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => changes.Add(e.Action);
        return (list, changes);
    }

    [Fact]
    public void TheSameItems_ChangeNothing()
    {
        var (list, changes) = Start(A, B, C);

        ObservableListReconciler.Apply(list, new[] { A, B, C });

        Assert.Empty(changes);
    }

    [Fact]
    public void ANewItemInFront_IsOneInsert()
    {
        var (list, changes) = Start(A, B, C);

        ObservableListReconciler.Apply(list, new[] { D, A, B, C });

        Assert.Equal(new[] { D, A, B, C }, list);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, changes);
    }

    [Fact]
    public void AnItemNoLongerWanted_IsOneRemove()
    {
        var (list, changes) = Start(A, B, C);

        ObservableListReconciler.Apply(list, new[] { A, C });

        Assert.Equal(new[] { A, C }, list);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove }, changes);
    }

    [Fact]
    public void AReorder_MovesItems_RatherThanReplacingThem()
    {
        var (list, changes) = Start(A, B, C);

        ObservableListReconciler.Apply(list, new[] { C, B, A });

        Assert.Equal(new[] { C, B, A }, list);
        Assert.All(changes, change => Assert.Equal(NotifyCollectionChangedAction.Move, change));
    }

    [Fact]
    public void MixedChanges_EndWithTheWantedList()
    {
        var (list, _) = Start(A, B, C);

        ObservableListReconciler.Apply(list, new[] { D, C, A });

        Assert.Equal(new[] { D, C, A }, list);
    }

    [Fact]
    public void AnEmptyListFills_AndEmptiesAgain()
    {
        var (list, _) = Start();

        ObservableListReconciler.Apply(list, new[] { A, B });
        Assert.Equal(new[] { A, B }, list);

        ObservableListReconciler.Apply(list, System.Array.Empty<Item>());
        Assert.Empty(list);
    }
}
