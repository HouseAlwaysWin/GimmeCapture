using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Linux V4L2 counterpart of the Windows dshow device handling in <see cref="Media.VideoInputDevices"/> /
/// <see cref="WebcamCaptureSource"/>. Enumerates <c>/dev/video*</c> capture nodes via the
/// <c>VIDIOC_QUERYCAP</c> ioctl — so metadata / output-only nodes (a UVC camera commonly exposes a second
/// non-capture node) are filtered out — and resolves a stored device name back to a <c>/dev/videoN</c> path
/// to open with the libav <c>v4l2</c> demuxer. Best-effort: any failure yields an empty list / null path
/// (i.e. no webcam PiP), never a throw.
/// </summary>
internal static class LinuxV4l2CaptureDevices
{
    // struct v4l2_capability { u8 driver[16]; u8 card[32]; u8 bus_info[32]; u32 version, capabilities, device_caps; u32 reserved[3]; } — 104 bytes
    [StructLayout(LayoutKind.Sequential)]
    private struct V4l2Capability
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] driver;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] card;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] bus_info;
        public uint version;
        public uint capabilities;
        public uint device_caps;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] reserved;
    }

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    private const uint V4L2_CAP_DEVICE_CAPS = 0x80000000;

    // VIDIOC_QUERYCAP = _IOR('V', 0, sizeof(v4l2_capability)) = (dir=READ<<30) | (size<<16) | ('V'<<8) | nr
    private const ulong VIDIOC_QUERYCAP = (2UL << 30) | (104UL << 16) | ((ulong)'V' << 8) | 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref V4l2Capability cap);

    /// <summary>A <c>/dev/video*</c> capture node with its friendly V4L2 card name.</summary>
    private readonly record struct CaptureNode(string Path, string Card);

    /// <summary>Enumerates capture-capable webcams as friendly names; genuine duplicates get a "(path)" suffix.</summary>
    public static IReadOnlyList<VideoInputDevice> Enumerate()
    {
        var result = new List<VideoInputDevice>();
        try
        {
            var nodes = ScanCaptureNodes();

            // Only disambiguate names that actually repeat (two identical cameras).
            var counts = nodes.GroupBy(n => n.Card, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes)
            {
                string display = counts[node.Card] > 1 ? $"{node.Card} ({node.Path})" : node.Card;
                result.Add(new VideoInputDevice(display));
            }
        }
        catch (Exception ex)
        {
            AppLog.Information($"LinuxV4l2.Enumerate failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Maps a stored WebcamDeviceName to the <c>/dev/videoN</c> path the v4l2 demuxer opens. Accepts a raw
    /// path, the disambiguated "Card (/dev/videoN)" form, or a bare card name (matched to the first capture
    /// node with that name). Returns null if nothing matches.
    /// </summary>
    public static string? ResolveDevicePath(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return null;
        }

        nameOrPath = nameOrPath.Trim();

        // Raw device path (also covers a hand-typed "/dev/videoN").
        if (nameOrPath.StartsWith("/dev/video", StringComparison.Ordinal))
        {
            return nameOrPath;
        }

        // Disambiguated "Card (/dev/videoN)" form: pull the path out of the trailing parens.
        int paren = nameOrPath.LastIndexOf("(/dev/video", StringComparison.Ordinal);
        if (paren >= 0 && nameOrPath.EndsWith(")", StringComparison.Ordinal))
        {
            return nameOrPath.Substring(paren + 1, nameOrPath.Length - paren - 2);
        }

        // Bare card name → first capture node advertising that card.
        try
        {
            foreach (var node in ScanCaptureNodes())
            {
                if (string.Equals(node.Card, nameOrPath, StringComparison.OrdinalIgnoreCase))
                {
                    return node.Path;
                }
            }
        }
        catch
        {
            // fall through to null
        }

        return null;
    }

    private static List<CaptureNode> ScanCaptureNodes()
    {
        var nodes = new List<CaptureNode>();

        string[] paths;
        try
        {
            paths = Directory.GetFiles("/dev", "video*");
        }
        catch
        {
            return nodes;
        }

        // Numeric order (video2 before video10) so the list is stable and matches `ls`.
        foreach (var path in paths.OrderBy(VideoNodeIndex))
        {
            string? card = TryQueryCaptureCard(path);
            if (card != null)
            {
                nodes.Add(new CaptureNode(path, card));
            }
        }

        return nodes;
    }

    private static int VideoNodeIndex(string path)
    {
        string name = Path.GetFileName(path); // "videoN"
        return name.Length > 5 && int.TryParse(name.AsSpan(5), out int n) ? n : int.MaxValue;
    }

    /// <summary>Returns the friendly card name if <paramref name="path"/> is a video-capture node, else null.</summary>
    private static string? TryQueryCaptureCard(string path)
    {
        int fd = open(path, O_RDONLY | O_NONBLOCK);
        if (fd < 0)
        {
            return null;
        }

        try
        {
            var cap = new V4l2Capability();
            if (ioctl(fd, VIDIOC_QUERYCAP, ref cap) < 0)
            {
                return null;
            }

            // device_caps describes this specific node; capabilities is the whole physical device.
            uint caps = (cap.capabilities & V4L2_CAP_DEVICE_CAPS) != 0 ? cap.device_caps : cap.capabilities;
            if ((caps & V4L2_CAP_VIDEO_CAPTURE) == 0)
            {
                return null; // output / metadata / m2m node — not a webcam capture source
            }

            string card = DecodeCString(cap.card);
            return string.IsNullOrWhiteSpace(card) ? Path.GetFileName(path) : card;
        }
        catch
        {
            return null;
        }
        finally
        {
            close(fd);
        }
    }

    private static string DecodeCString(byte[] buf)
    {
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0)
        {
            len = buf.Length;
        }

        return Encoding.UTF8.GetString(buf, 0, len).Trim();
    }
}
