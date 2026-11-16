using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace QDesktopExtension;

/// <summary>
/// Parsec 虚拟显示驱动的控制客户端（按官方 core/parsec-vdd.h 的 IOCTL 协议实现）。
/// 与旧驱动（MttVDD）的关键差别：显示器可以随时 add/remove，分辨率可以随时改，
/// 因此才能做到"一台客户端 = 一块自己的扩展屏"。
/// 注意：add 出来的显示器需要每 &lt;100ms 发一次 UPDATE 保活，否则 1 秒后自动拔掉。
/// </summary>
public sealed class ParsecVdd : IDisposable
{
    private const uint IoctlAdd = 0x0022e004;
    private const uint IoctlRemove = 0x0022a008;
    private const uint IoctlUpdate = 0x0022a00c;
    private const uint IoctlVersion = 0x0022e010;
    /// <summary>驱动支持的虚拟屏槽位数（扫描清理时要覆盖全部槽位）。</summary>
    public const int DriverSlots = 8;

    /// <summary>
    /// 本程序允许同时给客户端建的虚拟屏上限（全局唯一权威值）。
    /// 之前三处常量（3 / 8 / Screen.AllScreens &gt;= 9）互相矛盾，统一从这里取。
    /// </summary>
    public const int MaxClientScreens = 3;

    private static GuidEx AdapterGuid = new()
    {
        D1 = 0x00b41627, D2 = 0x04c4, D3 = 0x429e,
        D4 = new byte[] { 0xa2, 0x6e, 0x02, 0x65, 0xcf, 0x50, 0xc8, 0xfa }
    };

    private IntPtr _handle = new(-1);
    private Thread? _ping;
    private volatile bool _stop;

    // 句柄的"用"和"关"必须互斥：多线程同时 DeviceIoControl + CloseHandle 会让内核对象
    // 在使用中被销毁，表现为随机 0xc0000005（进程直接消失）。所有 IOCTL 都走这把锁。
    private readonly object _ioLock = new();

    public bool IsOpen => _handle != new IntPtr(-1) && _handle != IntPtr.Zero;

    /// <summary>打开驱动设备；失败返回 false（说明 Parsec VDD 驱动没装或不可用）。</summary>
    public bool Open()
    {
        lock (_ioLock)
        {
            if (IsOpen) return true;
            var h = OpenDevice();
            if (h == new IntPtr(-1) || h == IntPtr.Zero) return false;
            _handle = h;
            return true;
        }
    }

    /// <summary>
    /// 开始保活（每 80ms 一次 UPDATE）。必须放在残留屏清理之后调用：
    /// 驱动对 1 秒内收不到 UPDATE 的屏会自动拔掉，但新进程一旦开始保活，
    /// 上次崩溃留下的屏就会被一起"续命"，从此一直挂在系统里。
    /// </summary>
    public void StartKeepAlive()
    {
        if (!IsOpen || _ping != null) return;
        _stop = false;
        _ping = new Thread(PingLoop) { IsBackground = true, Name = "ParsecVddPing" };
        _ping.Start();
    }

    /// <summary>驱动版本（v0.45 返回 45）。失败返回 -1。</summary>
    public int Version => Ioctl(IoctlVersion, null);

    /// <summary>插一块虚拟显示器，返回索引；失败返回 -1。</summary>
    public int AddDisplay()
    {
        var idx = Ioctl(IoctlAdd, null);
        if (idx >= 0) Ioctl(IoctlUpdate, null);
        return idx;
    }

    /// <summary>拔掉指定索引的虚拟显示器。</summary>
    public bool RemoveDisplay(int index)
    {
        // 16 位大端索引（与官方头文件一致）
        var data = new byte[2];
        data[0] = (byte)((index >> 8) & 0xFF);
        data[1] = (byte)(index & 0xFF);
        var ok = Ioctl(IoctlRemove, data) >= 0;
        Ioctl(IoctlUpdate, null);
        return ok;
    }

    /// <summary>
    /// 清掉上一次进程（崩溃 / 被强杀 / 没来得及拔）留下的虚拟屏，返回清掉的块数。
    /// 每次启动先无条件盲扫一遍全部槽位（清幽灵槽位），之后只要桌面上还看得到残留就继续扫。
    /// 空槽位 REMOVE 只会返回失败，没有副作用。
    /// </summary>
    public int RemoveStaleDisplays()
    {
        if (!IsOpen) return 0;
        var before = VirtualDisplayMode.CountAttachedParsecScreens();

        // 第一轮无条件把全部槽位拔一遍：进程被强杀时驱动里可能留下"幽灵槽位"
        // （系统侧已经没有这块显示器了，但驱动仍认为该槽位被占用），
        // 靠枚举系统显示设备是发现不了的，不清掉会导致之后插屏一直插不出来。
        // 这一轮不设总时限：驱动在"这块屏拆不掉"时单次 REMOVE 会卡约 3 秒，
        // 8 个槽最坏 20 多秒——只会在系统显示栈已经卡死时出现，正常情况下几乎瞬时。
        for (var i = DriverSlots - 1; i >= 0; i--) RemoveDisplay(i);
        Thread.Sleep(200);

        // 单次 REMOVE 在某些驱动状态下会卡 1~3 秒，后续这轮重扫给个上限，
        // 免得残留屏太多时把服务启动（鉴权、推流）一直挡住
        var deadline = Environment.TickCount64 + 5000;

        // 拆除是异步的：系统里还能看到残留虚拟屏就继续扫，直到清干净或超时
        for (var round = 1; round < DriverSlots && Environment.TickCount64 < deadline; round++)
        {
            if (VirtualDisplayMode.CountAttachedParsecScreens() == 0) break;
            for (var i = DriverSlots - 1; i >= 0 && Environment.TickCount64 < deadline; i--) RemoveDisplay(i);
            Thread.Sleep(200);
        }

        return Math.Max(0, before - VirtualDisplayMode.CountAttachedParsecScreens());
    }

    private void PingLoop()
    {
        while (!_stop)
        {
            Ioctl(IoctlUpdate, null);
            Thread.Sleep(80);
        }
    }

    public void Dispose()
    {
        _stop = true;
        // 必须等保活线程真的退出：它可能正卡在 DeviceIoControl 里，
        // 此时关掉句柄就是典型的"使用已关闭的内核对象"，会直接崩进程。
        var exited = true;
        try { exited = _ping == null || _ping.Join(2000); } catch { }
        _ping = null;
        if (!exited)
        {
            // 线程还卡在驱动里：宁可泄漏这个句柄（进程马上就退出了），也不能关掉正在使用的句柄
            return;
        }
        lock (_ioLock)
        {
            if (_handle != new IntPtr(-1) && _handle != IntPtr.Zero)
            {
                try { CloseHandle(_handle); } catch { }
                _handle = new IntPtr(-1);
            }
        }
    }

    // ---------------- 原生互操作 ----------------

    [StructLayout(LayoutKind.Sequential)]
    public struct GuidEx
    {
        public uint D1; public ushort D2; public ushort D3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] D4;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInterfaceData
    {
        public uint cbSize;
        public GuidEx InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref GuidEx cls, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref GuidEx cls, uint index, ref SpDevInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDevInterfaceData data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr templ);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, uint inSize, out uint outBuf, uint outSize, IntPtr bytes, IntPtr overlapped);

    private static IntPtr OpenDevice()
    {
        var set = SetupDiGetClassDevs(ref AdapterGuid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return new IntPtr(-1);
        var d = new SpDevInterfaceData { cbSize = (uint)Marshal.SizeOf(typeof(SpDevInterfaceData)) };
        for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref AdapterGuid, i, ref d); i++)
        {
            uint need = 0;
            SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
            // need 为 0 / 过小时不能继续：AllocHGlobal(0) 拿到的是空块，
            // 后面 WriteInt32 + PtrToStringUni 会直接踩坏堆（0xc0000005 的常见来源）
            if (need < (uint)(IntPtr.Size == 8 ? 8 : 6)) continue;
            var buf = Marshal.AllocHGlobal((int)need);
            try
            {
                // 写 cbSize（x64 = 8，x86 = 6），路径字符串紧跟其后
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, IntPtr.Zero)) continue;
                var path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                if (string.IsNullOrEmpty(path)) continue;
                var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (h != new IntPtr(-1) && h != IntPtr.Zero)
                {
                    SetupDiDestroyDeviceInfoList(set);
                    return h;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        SetupDiDestroyDeviceInfoList(set);
        return new IntPtr(-1);
    }

    private int Ioctl(uint code, byte[]? data)
    {
        var inBuf = new byte[32];
        if (data != null) Array.Copy(data, inBuf, Math.Min(data.Length, 32));
        uint outVal = 0;
        // 所有 IOCTL 串行执行：并发 DeviceIoControl + CloseHandle 会让句柄在调用途中失效
        lock (_ioLock)
        {
            if (_handle == new IntPtr(-1) || _handle == IntPtr.Zero) return -1;
            if (!DeviceIoControl(_handle, code, inBuf, 32, out outVal, 4, IntPtr.Zero, IntPtr.Zero)) return -1;
        }
        return (int)outVal;
    }
}
