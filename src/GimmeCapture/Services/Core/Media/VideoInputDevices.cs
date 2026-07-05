using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

/// <summary>A selectable webcam / DirectShow video capture device (friendly name).</summary>
public sealed record VideoInputDevice(string Name);

/// <summary>
/// Enumerates video capture devices (webcams) for the Record settings UI, mirroring <c>AudioInputDevices</c>.
/// On Windows it uses libav's <c>avdevice_list_input_sources</c> on the dshow demuxer so the friendly names
/// returned match what <see cref="NativeFFmpeg.WebcamCaptureSource"/> opens via <c>video=&lt;name&gt;</c>; on
/// Linux it delegates to <see cref="NativeFFmpeg.LinuxV4l2CaptureDevices"/> (V4L2 <c>/dev/video*</c> nodes).
/// All failures are swallowed — enumeration is best-effort, and the user can still type a device name (or a
/// <c>/dev/videoN</c> path) by hand if the list comes back empty.
/// </summary>
public static class VideoInputDevices
{
    public static IReadOnlyList<VideoInputDevice> Enumerate()
    {
        // Linux has no dshow; enumerate V4L2 /dev/video* capture nodes instead.
        if (!OperatingSystem.IsWindows())
        {
            return NativeFFmpeg.LinuxV4l2CaptureDevices.Enumerate();
        }

        var devices = new List<VideoInputDevice>();

        try
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVInputFormat* ifmt = ffmpeg.av_find_input_format("dshow");
                if (ifmt == null)
                {
                    Debug.WriteLine("[Webcam] dshow demuxer unavailable for enumeration.");
                    return devices;
                }

                AVDeviceInfoList* list = null;
                int rc = ffmpeg.avdevice_list_input_sources(ifmt, null, null, &list);
                if (rc < 0 || list == null)
                {
                    Debug.WriteLine($"[Webcam] avdevice_list_input_sources failed ({rc}).");
                    return devices;
                }

                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < list->nb_devices; i++)
                    {
                        AVDeviceInfo* d = list->devices[i];
                        if (d == null)
                        {
                            continue;
                        }

                        // dshow lists both audio and video sources; keep only those advertising video.
                        // Older builds may not populate media_types — treat "unknown" as a candidate.
                        bool hasVideo = d->nb_media_types == 0;
                        for (int m = 0; m < d->nb_media_types; m++)
                        {
                            if (d->media_types[m] == AVMediaType.AVMEDIA_TYPE_VIDEO)
                            {
                                hasVideo = true;
                                break;
                            }
                        }

                        if (!hasVideo)
                        {
                            continue;
                        }

                        // The friendly description is what users recognise and what dshow opens by name;
                        // fall back to the unique device name if a description is missing.
                        string? name = PtrToStr(d->device_description);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = PtrToStr(d->device_name);
                        }

                        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name!))
                        {
                            devices.Add(new VideoInputDevice(name!));
                        }
                    }
                }
                finally
                {
                    ffmpeg.avdevice_free_list_devices(&list);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerate video devices failed: {ex.Message}");
        }

        return devices;
    }

    private static unsafe string? PtrToStr(byte* p)
        => p == null ? null : Marshal.PtrToStringAnsi((IntPtr)p);
}
