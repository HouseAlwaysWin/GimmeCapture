using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GimmeCapture.ViewModels.Shared;

/// <summary>
/// Brings a bound collection to new contents and order with as few changes as it can. Items that stay are moved
/// rather than removed and added again, so the view keeps their containers instead of rebuilding every one. Items
/// are compared by reference.
/// </summary>
internal static class ObservableListReconciler
{
    internal static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var wanted = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            T item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item))
            {
                continue;
            }

            int current = IndexOf(target, item, i + 1);
            if (current >= 0)
            {
                target.Move(current, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }

    private static int IndexOf<T>(ObservableCollection<T> list, T item, int start) where T : class
    {
        for (int i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
