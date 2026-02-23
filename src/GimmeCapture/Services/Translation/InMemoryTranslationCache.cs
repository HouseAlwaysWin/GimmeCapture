using System;
using System.Collections.Generic;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Translation;

public class InMemoryTranslationCache : ITranslationCache
{
    private const int MaxEntries = 1000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly Dictionary<string, (string Value, DateTime ExpiresAtUtc)> _items = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public bool TryGet(string key, out string value)
    {
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAtUtc > DateTime.UtcNow)
                {
                    value = entry.Value;
                    return true;
                }

                _items.Remove(key);
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
            _items[key] = (value, DateTime.UtcNow.Add(EntryTtl));
            _order.Enqueue(key);

            while (_items.Count > MaxEntries && _order.Count > 0)
            {
                var oldestKey = _order.Dequeue();
                _items.Remove(oldestKey);
            }
        }
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
