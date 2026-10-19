using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Nocturne.App.Controls;

public class MpvHwndHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int HOST_ID = 0x0002;

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        int dwStyle,
        int x, int y,
        int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public IntPtr Hwnd { get; private set; }

    public event Action<IntPtr>? HwndCreated;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Hwnd = CreateWindowEx(
            0,
            "static",
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0,
            Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            (IntPtr)HOST_ID,
            IntPtr.Zero,
            IntPtr.Zero);

        HwndCreated?.Invoke(Hwnd);
        return new HandleRef(this, Hwnd);
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (Hwnd != IntPtr.Zero)
        {
            var w = Math.Max(1, (int)rcBoundingBox.Width);
            var h = Math.Max(1, (int)rcBoundingBox.Height);
            SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
        Hwnd = IntPtr.Zero;
    }
}
