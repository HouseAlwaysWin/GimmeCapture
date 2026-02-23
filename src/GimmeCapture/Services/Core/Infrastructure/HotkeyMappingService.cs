using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Services.Core.Infrastructure;

public class HotkeyMappingService
{
    private static readonly Lazy<Dictionary<string, Action<MainWindowViewModel, string>>> HotkeySetters =
        new(BuildSetters);

    public void UpdateViewModelHotkey(MainWindowViewModel vm, string tag, string hotkeyStr)
    {
        if (vm == null || string.IsNullOrEmpty(tag)) return;

        if (HotkeySetters.Value.TryGetValue(tag, out var setter))
        {
            setter(vm, hotkeyStr);
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[HotkeyMappingService] Unknown hotkey tag: {tag}");
        }
    }

    public IReadOnlyList<string> ValidateTags(IEnumerable<string> tags)
    {
        if (tags == null) return Array.Empty<string>();

        var unknown = new List<string>();
        foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal))
        {
            if (!HotkeySetters.Value.ContainsKey(tag))
            {
                unknown.Add(tag);
            }
        }

        return unknown;
    }

    private static Dictionary<string, Action<MainWindowViewModel, string>> BuildSetters()
    {
        var result = new Dictionary<string, Action<MainWindowViewModel, string>>(StringComparer.Ordinal);
        var props = typeof(MainWindowViewModel)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetSetMethod() != null);

        foreach (var prop in props)
        {
            var vmParam = Expression.Parameter(typeof(MainWindowViewModel), "vm");
            var valueParam = Expression.Parameter(typeof(string), "value");
            var assign = Expression.Assign(Expression.Property(vmParam, prop), valueParam);
            var lambda = Expression.Lambda<Action<MainWindowViewModel, string>>(assign, vmParam, valueParam);
            result[prop.Name] = lambda.Compile();
        }

        return result;
    }
}
