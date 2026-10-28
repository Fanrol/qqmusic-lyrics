using System;
using System.Runtime.InteropServices;

namespace QQMusicLyrics.Interop;

/// <summary>浮窗需要的一点点 Win32 能力。刻意保持最小：只改扩展窗口样式、置顶、和光标位置。</summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;

    public const int WS_EX_TRANSPARENT = 0x00000020; // 鼠标穿透
    public const int WS_EX_LAYERED = 0x00080000;     // WPF 透明窗口本身已带
    public const int WS_EX_TOOLWINDOW = 0x00000080;  // 不出现在 Alt+Tab
    public const int WS_EX_NOACTIVATE = 0x08000000;  // 不抢焦点

    private static readonly IntPtr HwndTopMost = new(-1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 取光标在屏幕上的位置（物理像素）。
    /// 拖拽窗口时必须用屏幕坐标而不是"相对窗口的坐标"：窗口自己也在移动，
    /// 用相对坐标会形成反馈回路，实测位移会正好变成鼠标位移的一半。
    /// </summary>
    public static bool TryGetCursorPosition(out POINT point) => GetCursorPos(out point);

    public static void SetClickThrough(IntPtr hwnd, bool enable)
    {
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        int updated = enable
            ? style | WS_EX_TRANSPARENT | WS_EX_LAYERED
            : style & ~WS_EX_TRANSPARENT;
        if (updated != style)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, updated);
        }
    }

    /// <summary>标记为工具窗口并禁止激活：避免浮窗出现在 Alt+Tab、也不抢走游戏焦点。</summary>
    public static void ApplyOverlayStyles(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
    }

    /// <summary>
    /// 重新把自己压到置顶层的**最上面**。
    ///
    /// 只靠 <c>Window.Topmost</c> 不够：Windows 里"置顶"是一整层，多个置顶窗口之间仍按激活顺序排队，
    /// 别的置顶窗口（游戏浮层、QQ 音乐自己的歌词窗、某些播放器）一被激活就会跑到我们前面。
    /// 所以定时重申一次，才能真正做到"一直在最顶端"。
    /// 全程带 SWP_NOACTIVATE，不会抢焦点。
    /// </summary>
    public static void RaiseToTopMost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    public static void DestroyIconHandle(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }
}
