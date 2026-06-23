using System;
using System.Collections.Generic;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Pure index-lookup for an option within a list, matching by reference first
/// then by ordinal id equality. Extracted from
/// <c>MainWindowViewModel.SyncSelectedLlamaModelIndexCore</c> so the search can be
/// unit tested without a ViewModel. Generic to avoid coupling Infrastructure to
/// the VM-nested option type. Behavior-preserving — same ReferenceEquals fast
/// path and same <see cref="StringComparison.Ordinal"/> id comparison.
/// </summary>
public static class ModelOptionSelector
{
    /// <summary>
    /// Returns the index of <paramref name="option"/> within <paramref name="options"/>,
    /// matching by reference or by ordinal id equality. Returns -1 if the option is
    /// null or not present.
    /// </summary>
    public static int FindIndexById<T>(T? option, IReadOnlyList<T> options, Func<T, string> idSelector)
        where T : class
    {
        if (option is null) return -1;

        for (int i = 0; i < options.Count; i++)
        {
            if (ReferenceEquals(options[i], option)
                || string.Equals(idSelector(options[i]), idSelector(option), StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
