using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>
/// 判断当前会话是否处于锁屏（Win+L / 屏保锁屏）状态。
/// 锁屏后 Windows 切到安全桌面，任何截屏接口都拿不到画面，
/// 所以这里只负责"发现锁屏"，由 StreamServer 通知客户端显示占位提示。
/// </summary>
internal static class LockState
{
    private const int WTSSessionInfoEx = 25;
    private const int Level1 = 1;
    private const int SessionFlagsOffset = 12;   // Level(4) + SessionId(4) + SessionState(4)
    private const int WTS_SESSIONSTATE_LOCK = 0x1;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int wtsInfoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    /// <summary>
    /// 画面是否处于"拿不到"的状态：锁屏，或者屏幕被关闭 / 切到安全桌面
    /// （此时 BitBlt 会直接失败，客户端只能停在最后一帧，必须给用户一个明确提示）。
    /// </summary>
    public static bool IsScreenUnavailable()
    {
        if (IsLocked()) return true;
        // 锁屏时输入桌面是 Winlogon 的安全桌面，普通进程 OpenInputDesktop 会失败；
        // 显示器整体关闭 / 会话切换时同样拿不到，所以用它做兜底判断。
        IntPtr desktop = IntPtr.Zero;
        try
        {
            // 检查 OpenInputDesktop 的返回句柄：失败（锁屏/安全桌面/显示器关闭）时返回 NULL
            desktop = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
            return desktop == IntPtr.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (desktop != IntPtr.Zero)
            {
                try { CloseDesktop(desktop); } catch { }
            }
        }
    }

    public static bool IsLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!ProcessIdToSessionId(Process.GetCurrentProcess().Id, out var sessionId)) return false;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSSessionInfoEx, out buffer, out _)) return false;
            if (buffer == IntPtr.Zero) return false;

            var level = Marshal.ReadInt32(buffer, 0);
            if (level != Level1) return false;

            var flags = Marshal.ReadInt32(buffer, SessionFlagsOffset);
            return (flags & WTS_SESSIONSTATE_LOCK) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { WTSFreeMemory(buffer); } catch { }
            }
        }
    }
}
