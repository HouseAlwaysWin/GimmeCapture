using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Microsoft.Win32;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// What the recording hardware probe found on this machine, kept between runs. The probe loads FFmpeg, all seven
/// libraries, and test-opens the GPU's AV1 encoders, and the Record tab ran it the first time it was shown in every run:
/// FFmpeg then held about 26 MB of the process for the rest of the run although nothing was recorded. The answer is kept
/// beside the settings, which a new version reads from a folder of its own, so an update probes again, and under a key
/// made of the graphics adapters and their driver versions, so a new GPU or driver does too. On Linux there is no key
/// yet, and every run probes as before.
/// </summary>
internal sealed class HardwareCapabilityCache
{
    internal const string FileName = "hardware-capabilities.json";

    // Part of the key: raise it when the probe tests something new, so an answer the old probe reached is not reused.
    private const int ProbeVersion = 1;

    internal readonly record struct Capabilities(bool HardwareAv1, bool Gif);

    internal sealed record Entry(string Key, bool HardwareAv1, bool Gif);

    private readonly Func<string> _directory;
    private readonly Func<string?> _machineKey;
    private readonly Func<Capabilities?> _probe;
    private readonly Action<bool> _recallHardwareAv1;

    public HardwareCapabilityCache(IAppSettingsService settingsService)
        : this(
            () => settingsService.BaseDataDirectory,
            ReadMachineKey,
            ProbeWithFFmpeg,
            LibavRecordingEncoder.RecallHardwareAv1)
    {
    }

    internal HardwareCapabilityCache(
        Func<string> directory,
        Func<string?> machineKey,
        Func<Capabilities?> probe,
        Action<bool> recallHardwareAv1)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _machineKey = machineKey ?? throw new ArgumentNullException(nameof(machineKey));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _recallHardwareAv1 = recallHardwareAv1 ?? throw new ArgumentNullException(nameof(recallHardwareAv1));
    }

    /// <summary>
    /// The answer for this machine as it is now. The one an earlier run reached, while the key still matches, handed to
    /// <see cref="LibavRecordingEncoder"/> without loading FFmpeg; otherwise a fresh probe, which is kept for later runs.
    /// Null when the probe reached no answer (FFmpeg could not be loaded, or it threw), which is never kept. Reads,
    /// probes and writes files: call it off the UI thread.
    /// </summary>
    public Capabilities? Get()
    {
        string? key = TryReadMachineKey();
        string path = Path.Combine(_directory(), FileName);
        if (key != null && TryRead(path) is { } kept && kept.Key == key)
        {
            _recallHardwareAv1(kept.HardwareAv1);
            return new Capabilities(kept.HardwareAv1, kept.Gif);
        }

        if (_probe() is not { } found)
        {
            return null;
        }

        if (key != null)
        {
            TryWrite(path, new Entry(key, found.HardwareAv1, found.Gif));
        }

        return found;
    }

    /// <summary>
    /// The probe's version and the display adapters Windows has drivers for, each as its device id and driver
    /// version, in a stable order. Null off Windows.
    /// </summary>
    internal static string? ReadMachineKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? adapters = ReadDisplayAdapters();
        return adapters == null ? null : $"probe={ProbeVersion};{adapters}";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadDisplayAdapters()
    {
        // The display adapter device class: one numbered subkey per adapter driver installed.
        const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        using RegistryKey? displayClass = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (displayClass == null)
        {
            return null;
        }

        var adapters = new List<string>();
        foreach (string name in displayClass.GetSubKeyNames())
        {
            // Only the numbered adapter keys: the class also holds "Properties", which a user may not open.
            if (name.Length != 4 || !name.All(char.IsAsciiDigit))
            {
                continue;
            }

            try
            {
                using RegistryKey? adapter = displayClass.OpenSubKey(name);
                if (adapter?.GetValue("MatchingDeviceId") is string device && device.Length > 0)
                {
                    adapters.Add($"{device.ToLowerInvariant()}@{adapter.GetValue("DriverVersion") as string}");
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                AppLog.Debug("HardwareCapabilities.Adapter", ex);
            }
        }

        adapters.Sort(StringComparer.Ordinal);
        return string.Join("|", adapters);
    }

    // The probe the Record tab ran before: FFmpeg's AV1 test-open, then whether this FFmpeg build has a GIF encoder,
    // which needs FFmpeg loaded too. An answer only counts once the probe has really reached one: KnownHardwareAv1Usable
    // stays null when FFmpeg could not be loaded or the probe threw, and a "no" from either must not be kept.
    private static Capabilities? ProbeWithFFmpeg()
    {
        LibavRecordingEncoder.HasUsableHardwareAv1Encoder();
        if (LibavRecordingEncoder.KnownHardwareAv1Usable is not { } hardwareAv1)
        {
            return null;
        }

        return new Capabilities(hardwareAv1, RecordingFormatCapabilities.IsGifAvailable());
    }

    private string? TryReadMachineKey()
    {
        try
        {
            return _machineKey();
        }
        catch (Exception ex)
        {
            // No key means probing as before, and keeping nothing.
            AppLog.Debug("HardwareCapabilities.Key", ex);
            return null;
        }
    }

    private static Entry? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Debug("HardwareCapabilities.Read", ex);
            return null;
        }
    }

    private static void TryWrite(string path, Entry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(entry));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("HardwareCapabilities.Write", ex);
        }
    }
}
