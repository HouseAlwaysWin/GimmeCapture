using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// Every persisted setting has to survive save → load.
///
/// Loading deserializes into a fresh <see cref="AppSettings"/> and then copies it, field by field, into the live
/// instance (<see cref="AppSettingsService.UpdateSettings"/>). That hand-written copy fell behind the model: settings
/// and hotkeys added later were never copied, so they quietly reset to their defaults on every launch — and the next
/// save wrote the defaults back over what the user had chosen. This sets every persisted property to a non-default
/// value and checks that all of them come back, so a setting added without its copy line fails here instead of in
/// somebody's config.
/// </summary>
public sealed class AppSettingsLoadRoundTripTests : IDisposable
{
    /// <summary>Deliberately rewritten on load, not lost — each has its own dedicated test.</summary>
    private static readonly HashSet<string> RewrittenOnLoad = new(StringComparer.Ordinal)
    {
        nameof(AppSettings.ConfigVersion),             // SaveAsync stamps the current version
        nameof(AppSettings.SelectedTranslationEngine), // forced to LlamaSharp, the only engine left
        nameof(AppSettings.LlamaModelId),              // normalized against the model catalog
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsLoadRoundTripTests()
    {
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public async Task EveryPersistedSettingSurvivesSaveThenLoad()
    {
        var writer = new AppSettingsService(_dir);
        ChangeEveryPersistedProperty(writer.Settings, prefix: string.Empty);
        await writer.SaveAsync();

        var reader = new AppSettingsService(_dir);
        await reader.LoadAsync();

        var lost = new List<string>();
        CollectDifferences(writer.Settings, reader.Settings, prefix: string.Empty, lost);

        Assert.True(
            lost.Count == 0,
            $"{lost.Count} setting(s) did not survive save → load:\n  " + string.Join("\n  ", lost));
    }

    private static IEnumerable<PropertyInfo> PersistedProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null);

    private static void ChangeEveryPersistedProperty(object target, string prefix)
    {
        foreach (var property in PersistedProperties(target.GetType()))
        {
            string name = prefix + property.Name;
            if (RewrittenOnLoad.Contains(name))
            {
                continue;
            }

            object? current = property.GetValue(target);
            Type type = property.PropertyType;

            if (type == typeof(bool)) property.SetValue(target, !(bool)current!);
            else if (type == typeof(int)) property.SetValue(target, (int)current! + 7);
            else if (type == typeof(double)) property.SetValue(target, (double)current! + 1.5);
            else if (type == typeof(string)) property.SetValue(target, $"rt-{name}");
            else if (type.IsEnum) property.SetValue(target, NextDefinedValue(type, current!));
            else if (type.IsClass && current != null) ChangeEveryPersistedProperty(current, name + ".");
            else throw new NotSupportedException($"{name}: teach this test how to change a {type.Name}.");
        }
    }

    private static object NextDefinedValue(Type enumType, object current)
    {
        var values = Enum.GetValues(enumType);
        int index = Array.IndexOf(values, current);
        return values.GetValue((index + 1) % values.Length)!;
    }

    private static void CollectDifferences(object expected, object actual, string prefix, List<string> lost)
    {
        foreach (var property in PersistedProperties(expected.GetType()))
        {
            string name = prefix + property.Name;
            if (RewrittenOnLoad.Contains(name))
            {
                continue;
            }

            object? saved = property.GetValue(expected);
            object? loaded = property.GetValue(actual);

            if (property.PropertyType.IsClass && property.PropertyType != typeof(string) && saved != null && loaded != null)
            {
                CollectDifferences(saved, loaded, name + ".", lost);
            }
            else if (!Equals(saved, loaded))
            {
                lost.Add($"{name}: saved '{saved}', loaded '{loaded}'");
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort temp cleanup */ }
    }
}
