using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace GimmeCapture.Tests;

/// <summary>
/// A hidden borderless Unicode window for tests that need a real one, owned by the thread that creates it. Its client
/// area is its whole screen rectangle; its procedure answers every hit test with <see cref="OwnHitTestAnswer"/> and
/// records the characters and the style changes it is sent.
/// </summary>
internal sealed class Win32TestWindow : IDisposable
{
    public const int OwnHitTestAnswer = 42;

    private const uint WS_POPUP = 0x80000000;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_STYLECHANGED = 0x007D;
    private static int s_classCount;

    private readonly string _className = $"GimmeCaptureTestWindow{Interlocked.Increment(ref s_classCount)}";
    private readonly WndProc _proc; // Windows calls it while the window exists

    public Win32TestWindow(int left, int top, int width, int height)
    {
        _proc = Procedure;
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception();
        }

        Handle = CreateWindowEx(
            0, _className, string.Empty, WS_POPUP, left, top, width, height,
            IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new Win32Exception();
        }
    }

    public IntPtr Handle { get; }

    public List<char> Chars { get; } = new();

    public int StyleChanges { get; private set; }

    public void Dispose()
    {
        DestroyWindow(Handle);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    private IntPtr Procedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return new IntPtr(OwnHitTestAnswer);
            case WM_CHAR:
                Chars.Add((char)wParam.ToInt64());
                return IntPtr.Zero;
            case WM_STYLECHANGED:
                StyleChanges++;
                break;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
