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

    private const int DmPosition = 0x20;
    private const int DmPelsWidth = 0x80000;
    private const int DmPelsHeight = 0x100000;
    private const int CdsUpdateregistry = 0x1;

    /// <summary>把指定显示器移到"现有桌面最右侧"，只动这一块。</summary>
    public static bool PlaceRightOfDesktop(string deviceName, out string message)
    {
        message = "";
        try
        {
            var right = 0;
            foreach (var s in Screen.AllScreens)
            {
                if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                right = Math.Max(right, s.Bounds.X + s.Bounds.Width);
            }

            var dm = new DevMode { dmSize = (short)Marshal.SizeOf(typeof(DevMode)) };
            if (!EnumDisplaySettings(deviceName, -1, ref dm))
            {
                message = "读不到该显示器的当前模式";
                return false;
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
