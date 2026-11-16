using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QDesktopExtension;

/// <summary>
/// 把"客户端屏幕分辨率"变成虚拟显示器上的真实模式。
/// 虚拟显示驱动只在设备重载时读取 vdd_settings.xml 里的模式列表，所以新增分辨率的流程是：
///   写配置 → 重载设备（不重启电脑）→ 切到该分辨率。
/// 重载设备需要管理员权限，因此首次遇到新分辨率时会弹一次 UAC；之后该分辨率已存在，静默即可。
/// </summary>
public static class VirtualDisplayMode
{
    private const string XmlPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr lparam);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplayDevices(string? dev, uint num, ref DISPLAY_DEVICE dd, uint flags);

    /// <summary>
    /// 这个显示器名对应的显示适配器是不是 Parsec 虚拟显示适配器。
    /// 必须查适配器节点（DeviceString = "Parsec Virtual Display Adapter"）：
    /// 显示器节点上拿到的只是 "Generic PnP Monitor"，查不出虚拟屏。
    /// </summary>
    public static bool IsParsecAdapter(string deviceName)
    {
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
            if (!string.Equals(dd.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
            var s = (dd.DeviceString ?? "") + "|" + (dd.DeviceID ?? "");
            return s.Contains("Parsec", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// 当前真正挂在桌面上的 Parsec 虚拟屏数量。
    /// 注意不能数适配器：驱动给每块屏注册的设备节点会作为"幻影设备"永久留在系统里，
    /// 拔掉屏之后依然能被枚举出来（只是 flags 里没有 ATTACHED），数出来会一直偏大。
    /// </summary>
    public static int CountAttachedParsecScreens()
    {
        var count = 0;
        foreach (var sc in Screen.AllScreens)
        {
            if (IsParsecAdapter(sc.DeviceName)) count++;
        }
        return count;
    }

    /// <summary>这个显示器是不是那块虚拟显示器（不是的话不能动它的分辨率）。</summary>
    public static bool IsVirtualMonitor(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex < 0 || monitorIndex >= screens.Length) return false;
        var name = screens[monitorIndex].DeviceName;
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
        if (!EnumDisplayDevices(name, 0, ref dd, 0)) return false;
        // 虚拟显示器的 EDID 硬件 ID 是 MTT1337（驱动名可能显示成 "Generic PnP Monitor"）。
        // 这里只认 MttVDD：Parsec VDD 虽然也带 "VDD" 字样，但它不读 vdd_settings.xml，
        // 走下面的加模式流程只会白弹一次 UAC 并卡住管线，必须排除。
        var s = (dd.DeviceString ?? "") + "|" + (dd.DeviceID ?? "");
        return s.Contains("MTT1337") || s.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这个显示器是不是 Parsec 虚拟屏（我们自己给客户端插的那块，不能当共享画面源）。</summary>
    public static bool IsParsecMonitor(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex < 0 || monitorIndex >= screens.Length) return false;
        return IsParsecAdapter(screens[monitorIndex].DeviceName);
    }

    public static bool HasMode(string device, int w, int h)
    {
        for (var i = 0; i < 400; i++)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            if (!EnumDisplaySettings(device, i, ref dm)) break;
            if (dm.dmPelsWidth == w && dm.dmPelsHeight == h) return true;
        }
        return false;
    }

    /// <summary>把虚拟屏切到 w×h；成功返回 true。需要新增模式时会弹一次 UAC。</summary>
    public static bool EnsureMode(int monitorIndex, int w, int h, string toolDir, Logger logger)
    {
        try
        {
            if (!IsVirtualMonitor(monitorIndex))
            {
                logger.Info("当前采集的不是虚拟显示器，跳过分辨率切换（保持等比缩放）");
                return false;
            }

            var device = Screen.AllScreens[monitorIndex].DeviceName;
            if (HasMode(device, w, h) && SetMode(device, w, h))
            {
                logger.Info($"虚拟屏已切到 {w}x{h}（已有模式）");
                return true;
            }

            // 驱动里没有这个模式：改配置 + 重载设备（需要管理员）
            var script = Path.Combine(toolDir, "vdd-addmode.ps1");
            if (!File.Exists(script))
            {
                logger.Warn($"缺少 {script}，无法添加分辨率模式");
                return false;
            }

            logger.Info($"驱动里没有 {w}x{h}，正在以管理员身份添加该模式（会弹一次 UAC）…");
            var resultFile = Path.Combine(Path.GetTempPath(), "qde-vdd-mode.result");
            try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Width {w} -Height {h}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) return false;
                if (!p.WaitForExit(45000))
                {
                    logger.Warn("添加分辨率超时");
                    return false;
                }
            }

            // 设备重载后显示器编号可能变，重新按"虚拟屏"找一次并确认模式
            for (var i = 0; i < 6; i++)
            {
                foreach (var sc in Screen.AllScreens)
                {
                    if (!IsVirtualMonitor(Array.IndexOf(Screen.AllScreens, sc))) continue;
                    if (HasMode(sc.DeviceName, w, h) && SetMode(sc.DeviceName, w, h))
                    {
                        logger.Info($"虚拟屏已切到 {w}x{h}（新添加的模式）");
                        return true;
                    }
                }
                System.Threading.Thread.Sleep(1500);
            }

            logger.Warn($"添加模式 {w}x{h} 后仍未切换成功");
            return false;
        }
        catch (Exception ex)
        {
            logger.Warn($"切换虚拟屏分辨率失败: {ex.Message}");
            return false;
        }
    }

    private static bool SetMode(string device, int w, int h)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
        EnumDisplaySettings(device, -1, ref dm);
        dm.dmPelsWidth = w;
        dm.dmPelsHeight = h;
        dm.dmFields = 0x80000 | 0x100000;   // DM_PELSWIDTH | DM_PELSHEIGHT（0x100000 不是 DM_BITSPERPEL）
        return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0x1, IntPtr.Zero) == 0;
    }
}
