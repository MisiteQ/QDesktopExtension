using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QDesktopExtension;

public sealed record WindowInfo(long Handle, string Title, string Process, int Width, int Height, bool Minimized)
{
    public override string ToString() =>
        $"{(Minimized ? "[最小化] " : "")}{Title} ({Process}, {Width}x{Height})";
}

/// <summary>
/// 单个窗口的采集：PrintWindow(PW_RENDERFULLCONTENT) 抓客户区，
/// 失败（部分 OpenGL/硬件渲染窗口）时退回窗口 DC 的 BitBlt。
/// 只抓客户区而不是整窗，是因为标题栏/边框对远程操作没有意义，还浪费像素。
/// </summary>
public static class WindowSource
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint SRCCOPY = 0x00CC0020;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder buf, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                      IntPtr hdcSrc, int srcX, int srcY, uint rop);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    /// <summary>
    /// 虚拟扩展屏里点某个格子前，先在桌面把那个窗口提到最前——
    /// 否则鼠标注入会落到盖在它上面的其他窗口，等于点错程序。
    /// </summary>
    public static void Activate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, 9);            // SW_RESTORE
            // SetForegroundWindow 在后台进程里常被系统拒绝，先用 TOPMOST 再撤回 NOTOPMOST，
            // 这一步能把窗口强制拉到 z 序最前，点击才不会被别的窗口吃掉
            SetWindowPos(hWnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);  // HWND_TOPMOST + NOACTIVATE
            SetWindowPos(hWnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);  // HWND_NOTOPMOST
            SetForegroundWindow(hWnd);
        }
        catch
        {
        }
    }

    /// <summary>窗口客户区在虚拟桌面里的位置（输入坐标映射与采集区域都用它）。</summary>
    public static ScreenRect? GetClientScreenRect(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return null;
        RECT rc;
        if (!GetClientRect(hWnd, out rc)) return null;
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;
        var pt = new POINT();
        if (!ClientToScreen(hWnd, ref pt)) return null;
        return new ScreenRect(pt.X, pt.Y, w, h);
    }

    /// <summary>整个窗口（含边框）在桌面上的矩形。</summary>
    public static ScreenRect? GetWindowRectOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return null;
        RECT rc;
        if (!GetWindowRect(hWnd, out rc)) return null;
        var w = rc.Right - rc.Left;
        var h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return null;
        return new ScreenRect(rc.Left, rc.Top, w, h);
    }

    /// <summary>
    /// 把窗口挪到指定位置。用来把窗口"搬到扩展屏"——也就是挪到物理屏幕之外，
    /// 主屏上就看不见它了，跟拖到第二块显示器上是同一个效果。
    /// </summary>
    public static bool MoveTo(IntPtr hWnd, int x, int y, int w, int h)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, 9);   // SW_RESTORE
            return SetWindowPos(hWnd, IntPtr.Zero, x, y, Math.Max(160, w), Math.Max(120, h),
                                0x0004 | 0x0010);   // SWP_NOZORDER | SWP_NOACTIVATE
        }
        catch
        {
            return false;
        }
    }

    public static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        GetWindowTextW(hWnd, sb, 512);
        return sb.ToString();
    }

    public static bool IsMinimized(IntPtr hWnd) => IsIconic(hWnd);

    /// <summary>可采集的顶层窗口列表（按 z 序，越靠前越在上层）。</summary>
    public static List<WindowInfo> List(uint excludePid = 0)
    {
        var list = new List<WindowInfo>();
        var proc = new EnumProc((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd)) return true;
                if ((GetWindowLongW(hWnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

                // UWP 的挂起窗口/其他虚拟桌面的窗口会"cloaked"，抓出来是黑的
                var cloaked = 0;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                var title = GetTitle(hWnd);
                if (title.Length == 0) return true;

                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (excludePid != 0 && pid == excludePid) return true;

                RECT wr;
                if (!GetWindowRect(hWnd, out wr)) return true;
                var w = wr.Right - wr.Left;
                var h = wr.Bottom - wr.Top;
                if (w < 160 || h < 120) return true;      // 托盘气泡、提示条之类

                var rect = GetClientScreenRect(hWnd);
                if (rect == null && !IsIconic(hWnd)) return true;

                var procName = "";
                try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }

                list.Add(new WindowInfo(hWnd.ToInt64(), title,
                                        procName,
                                        rect?.Width ?? w, rect?.Height ?? h,
                                        IsIconic(hWnd)));
            }
            catch
            {
            }
            return true;
        });
        EnumWindows(proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    /// <summary>抓一帧客户区内容到目标 DC。返回 false 表示这帧没抓到。</summary>
    public static bool CaptureTo(IntPtr hWnd, IntPtr memDc, int width, int height)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;

        // 首选：DWM 合成内容，窗口被遮挡/最小化也能拿到（Chrome/Electron/WPF 都走路）
        if (PrintWindow(hWnd, memDc, PW_RENDERFULLCONTENT))
        {
            // PrintWindow 返回非零但仍可能是空实现：这里不额外判断，交给上层按"画面是否变化"兜
            return true;
        }

        // 兜底：直接拷窗口 DC（OpenGL 子窗口、部分游戏/视频窗口只在这个路径里有内容）
        var dc = GetWindowDC(hWnd);
        if (dc == IntPtr.Zero) return false;
        try
        {
            var ok = BitBlt(memDc, 0, 0, width, height, dc, 0, 0, SRCCOPY);
            return ok;
        }
        finally
        {
            ReleaseDC(hWnd, dc);
        }
    }
}
