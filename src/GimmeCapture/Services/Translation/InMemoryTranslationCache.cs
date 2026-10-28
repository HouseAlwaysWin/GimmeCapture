using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Translation;

/// <summary>
/// Translations already made, kept for <see cref="EntryTtl"/> after they were written and at most
/// <see cref="DefaultMaxEntries"/> of them. Past that, the entry written longest ago goes first. Writing a key again
/// counts as a new write: it gets a fresh lifetime and goes to the back of that order. Reading changes neither.
/// </summary>
public class InMemoryTranslationCache : ITranslationCache
{
    private const int DefaultMaxEntries = 1000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly int _maxEntries;
    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<string, (string Value, DateTime ExpiresAtUtc, LinkedListNode<string> Order)> _items = new(StringComparer.Ordinal);

    // One place per cached key, oldest write first. A queue that got a second place for a rewritten key grew without
    // bound, and evicting that key's old place threw out the value just written as if it were the oldest.
    private readonly LinkedList<string> _order = new();

    public InMemoryTranslationCache()
        : this(DefaultMaxEntries, static () => DateTime.UtcNow)
    {
    }

    internal InMemoryTranslationCache(int maxEntries, Func<DateTime> utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _maxEntries = maxEntries;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>Places in the eviction order, which should hold one per cached key.</summary>
    internal int OrderCountForTest
    {
        get
        {
            lock (_lock)
            {
                return _order.Count;
            }
        }
    }

    public bool TryGet(string key, out string value)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAtUtc > _utcNow())
                {
                    value = entry.Value;
                    return true;
                }

                Remove(key, entry.Order);
            }
        }

        value = string.Empty;
        return false;
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;

        lock (_lock)
        {
            DateTime expiresAtUtc = _utcNow().Add(EntryTtl);
            if (_items.TryGetValue(key, out var existing))
            {
                _order.Remove(existing.Order);
                _order.AddLast(existing.Order);
                _items[key] = (value, expiresAtUtc, existing.Order);
                return;
            }

            _items[key] = (value, expiresAtUtc, _order.AddLast(key));
            while (_items.Count > _maxEntries)
            {
                var oldest = _order.First!;
                Remove(oldest.Value, oldest);
            }
        }
    }

    private void Remove(string key, LinkedListNode<string> order)
    {
        _order.Remove(order);
        _items.Remove(key);
    }

    public string BuildKey(TranslationEngine engine, OCRLanguage sourceLang, TranslationLanguage targetLang, string text)
    {
        var normalized = NormalizeText(text);
        return $"{engine}|{sourceLang}|{targetLang}|{normalized}";
    }

    private static string NormalizeText(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Trim().ToLowerInvariant();
    }
}
