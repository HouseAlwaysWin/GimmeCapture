using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace GimmeCapture.Services.Core.Infrastructure;

public class HotkeyMappingService
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, Action<object, string>>> HotkeySettersByType = new();

    public void UpdateViewModelHotkey(object target, string tag, string hotkeyStr)
    {
        if (target == null || string.IsNullOrEmpty(tag)) return;

        var setters = GetSetters(target.GetType());
        if (setters.TryGetValue(tag, out var setter))
        {
            setter(target, hotkeyStr);
        }
        else
        {
            AppLog.Information("HotkeyMapping.UnknownTag");
        }
    }

    public IReadOnlyList<string> ValidateTags(IEnumerable<string> tags, object target)
    {
        if (tags == null || target == null) return Array.Empty<string>();

        var setters = GetSetters(target.GetType());
        var unknown = new List<string>();
        foreach (var tag in tags.AsValueEnumerable().Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal))
        {
            if (!setters.ContainsKey(tag))
            {
                unknown.Add(tag);
            }
        }

        return unknown;
    }

    private static Dictionary<string, Action<object, string>> GetSetters(Type targetType)
    {
        return HotkeySettersByType.GetOrAdd(targetType, BuildSetters);
    }

    private static Dictionary<string, Action<object, string>> BuildSetters(Type targetType)
    {
        var result = new Dictionary<string, Action<object, string>>(StringComparer.Ordinal);
        var props = targetType
            .GetProperties()
            .AsValueEnumerable()
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetSetMethod() != null);

        foreach (var prop in props)
        {
            var targetParam = Expression.Parameter(typeof(object), "target");
            var valueParam = Expression.Parameter(typeof(string), "value");
            var typedTarget = Expression.Convert(targetParam, targetType);
            var assign = Expression.Assign(Expression.Property(typedTarget, prop), valueParam);
            var lambda = Expression.Lambda<Action<object, string>>(assign, targetParam, valueParam);
            result[prop.Name] = lambda.Compile();
        }

        return result;
    }
}
