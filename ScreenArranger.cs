using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>
 /// 只管"新加出来的那块虚拟屏"摆位：把它放到现有桌面的最右侧，不碰任何已有显示器。
 /// 系统里已有的屏（笔记本内屏、外接显示器）的分辨率、坐标、主屏标记一律不动。
/// </summary>
public static class ScreenArranger
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string dev, int mode, ref DevMode dm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string dev, ref DevMode dm, IntPtr hwnd, int flags, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? dev, uint num, ref DisplayDevice dd, uint flags);

    private const int DisplayDeviceAttachedToDesktop = 0x1;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPath, IntPtr pathArray, uint numMode, IntPtr modeArray, uint flags);

    private const uint SdcTopologyExtend = 0x4;
    private const uint SdcApply = 0x80;

    /// <summary>
    /// 让 Windows 把"已插进驱动、但没接进桌面"的显示器接进来，等价于 Win+P → 扩展。
    /// 为什么不能只对那块屏调 ChangeDisplaySettingsEx：它还没被任何桌面拓扑引用，
    /// 系统会把它当成不可用目标，对它的模式设置一律返回 -1（本机实测三种模式都是 -1）。
    /// 改成扩展拓扑后，本机实测几秒内屏就出现了。
    /// </summary>
    public static bool ExtendDesktop(out string message)
    {
        message = "";
        try
        {
            var rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SdcTopologyExtend | SdcApply);
            if (rc != 0)
            {
                message = $"扩展桌面失败（SetDisplayConfig 返回 {rc}）";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 等一块"新出现的 Parsec 虚拟屏"进入桌面（Screen.AllScreens）。
    /// before 是插屏前的显示器名单；期间反复尝试扩展到所有可用显示，
    /// 系统完成拓扑切换是异步的，所以要轮询而不是调一次就断言失败。
    /// 只认 Parsec 的屏，绝不把用户原有的显示器当成新屏。
    /// </summary>
    public static bool AttachNewParsecScreen(ISet<string> before, int timeoutMs, out string deviceName, out string message)
    {
        deviceName = "";
        message = "";
        var deadline = Environment.TickCount64 + timeoutMs;
        var nextExtend = 0L;
        while (true)
        {
            var found = Screen.AllScreens.FirstOrDefault(x =>
                !before.Contains(x.DeviceName) && VirtualDisplayMode.IsParsecAdapter(x.DeviceName));
            if (found != null)
            {
                deviceName = found.DeviceName;
                message = $"{found.Bounds.Width}x{found.Bounds.Height}";
                return true;
            }
            var now = Environment.TickCount64;
            if (now >= deadline) break;
            // 拓扑切换每次都要重建桌面表面，至少间隔 2 秒，别把显示栈刷爆
            if (now >= nextExtend)
            {
                nextExtend = now + 2000;
                if (!ExtendDesktop(out var err) && err.Length > 0) message = err;
            }
            Thread.Sleep(200);
        }
        if (message.Length == 0) message = "扩展桌面后仍没有出现新的虚拟屏";
        return false;
    }

    /// <summary>列出当前所有显示设备（排查"插了屏但系统没挂到桌面"时用）。</summary>
    public static string DescribeDisplayDevices()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            for (uint i = 0; ; i++)
            {
                var dd = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                sb.Append($"[{i}] {dd.DeviceName} '{dd.DeviceString}' flags=0x{dd.StateFlags:X}  ");
            }
        }
        catch (Exception ex)
        {
            sb.Append("枚举失败: " + ex.Message);
        }
        return sb.ToString();
    }

    private const int DmPosition = 0x20;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int CdsUpdateregistry = 0x1;

    /// <summary>
    /// 现有桌面的最右边界，单位是物理像素（DEVMODE 坐标系）。
    /// 不能用 Screen.Bounds 算：本机 150% 缩放时 Screen.Bounds 是虚拟化后的 1493，
    /// 而 DM_POSITION 要的是物理 2240，混用会把虚拟屏摆到主屏里面去（两屏重叠）。
    /// excludeDevice 是要摆的那块屏自己，必须排除，否则会把它自己的宽度也算进去。
    /// </summary>
    private static int DesktopRightPhysical(string excludeDevice)
    {
        var right = 0;
        for (uint i = 0; ; i++)
        {
            var dd = new DisplayDevice { cb = Marshal.SizeOf(typeof(DisplayDevice)) };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
            if (string.Equals(dd.DeviceName, excludeDevice, StringComparison.OrdinalIgnoreCase)) continue;
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf(typeof(DevMode)) };
            if (!EnumDisplaySettings(dd.DeviceName, -1, ref dm)) continue;
            right = Math.Max(right, dm.dmPositionX + dm.dmPelsWidth);
        }
        return right;
    }

    /// <summary>把指定显示器移到"现有桌面最右侧"，只动这一块。</summary>
    public static bool PlaceRightOfDesktop(string deviceName, out string message)
    {
        message = "";
        try
        {
            var right = DesktopRightPhysical(deviceName);

            var dm = new DevMode { dmSize = (short)Marshal.SizeOf(typeof(DevMode)) };
            if (!EnumDisplaySettings(deviceName, -1, ref dm))
            {
                message = "读不到该显示器的当前模式";
                return false;
            }

            // 已经在最右侧就不动它：避免多余的显示栈重建（每次都会闪一下）
            if (dm.dmPositionX == right && dm.dmPositionY == 0)
            {
                message = $"已在 x={right}";
                return true;
            }

            dm.dmPositionX = right;
            dm.dmPositionY = 0;
            dm.dmFields = DmPosition | DmPelsWidth | DmPelsHeight;
            var rc = ChangeDisplaySettingsEx(deviceName, ref dm, IntPtr.Zero, CdsUpdateregistry, IntPtr.Zero);
            if (rc != 0)
            {
                message = $"摆放失败（ChangeDisplaySettingsEx 返回 {rc}）";
                return false;
            }

            message = $"已摆到 x={right}";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
