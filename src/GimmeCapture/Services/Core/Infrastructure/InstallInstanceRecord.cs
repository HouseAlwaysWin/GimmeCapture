using System;
using System.IO;
using System.Text.Json;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// Which folder an instance folder's install runs from. The instance key is a one-way hash of that folder, so without
/// this record nothing can tell another install's data from an install that is gone: every build folder, publish
/// folder and copy the app ever ran from keeps its own settings and AI modules there. Each start writes its own.
/// </summary>
internal sealed record InstallInstanceRecord(string AppDirectory, string Version, DateTime LastRunUtc)
{
    public const string FileName = "install.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write(string instanceDirectory, InstallInstanceRecord record)
    {
        AtomicFile.WriteAllText(Path.Combine(instanceDirectory, FileName), JsonSerializer.Serialize(record, Options));
    }

    /// <summary>The record in <paramref name="instanceDirectory"/>, or null for one written before records existed
    /// or one that cannot be read.</summary>
    public static InstallInstanceRecord? Read(string instanceDirectory)
    {
        string path = Path.Combine(instanceDirectory, FileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<InstallInstanceRecord>(File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
