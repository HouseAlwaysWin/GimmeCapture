using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace GimmeCapture.Services.Core.Media;

/// <summary>A selectable microphone / capture endpoint (id + friendly name).</summary>
public sealed record AudioInputDevice(string Id, string Name);

/// <summary>Enumerates active WASAPI capture (input) devices for the Record settings UI.</summary>
public static class AudioInputDevices
{
    /// <summary>
    /// Returns the active capture endpoints. The first entry is always the system-default
    /// device (empty id), so an empty <c>SelectedMicDeviceId</c> follows the OS default.
    /// </summary>
    public static IReadOnlyList<AudioInputDevice> Enumerate()
    {
        var devices = new List<AudioInputDevice> { new(string.Empty, "Default") };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try
                {
                    devices.Add(new AudioInputDevice(device.ID, device.FriendlyName));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerate input devices failed: {ex.Message}");
        }

        return devices;
    }
}
