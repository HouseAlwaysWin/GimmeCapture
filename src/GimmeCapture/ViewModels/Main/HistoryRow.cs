using System;
using System.Collections.Generic;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// One row of the history grid: the cards that fit side by side. The grid is a virtualizing list of these rows, so
/// only the rows on screen get cards built. As a wrap panel it built every card, at about 10 ms each, the moment the
/// tab opened.
/// </summary>
public sealed class HistoryRow
{
    private HistoryRow(IReadOnlyList<HistoryItemViewModel> items)
    {
        Items = items;
    }

    public IReadOnlyList<HistoryItemViewModel> Items { get; }

    /// <summary>
    /// Splits <paramref name="items"/> into rows of <paramref name="columns"/> cards. A row of
    /// <paramref name="previous"/> that holds exactly the same items at the same position is reused, so the view keeps
    /// its cards.
    /// </summary>
    internal static List<HistoryRow> Build(
        IReadOnlyList<HistoryItemViewModel> items,
        int columns,
        IReadOnlyList<HistoryRow> previous)
    {
        columns = Math.Max(1, columns);
        var rows = new List<HistoryRow>((items.Count + columns - 1) / columns);
        for (int start = 0; start < items.Count; start += columns)
        {
            int count = Math.Min(columns, items.Count - start);
            int index = rows.Count;
            if (index < previous.Count && Holds(previous[index], items, start, count))
            {
                rows.Add(previous[index]);
                continue;
            }

            var slice = new HistoryItemViewModel[count];
            for (int i = 0; i < count; i++)
            {
                slice[i] = items[start + i];
            }

            rows.Add(new HistoryRow(slice));
        }

        return rows;
    }

    private static bool Holds(HistoryRow row, IReadOnlyList<HistoryItemViewModel> items, int start, int count)
    {
        if (row.Items.Count != count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!ReferenceEquals(row.Items[i], items[start + i]))
            {
                return false;
            }
        }

        return true;
    }
}
