using System.Runtime.InteropServices;

namespace QDesktopExtension;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}@({X},{Y})";
}

public static class Dpi
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>
    /// 必须在任何窗口/采集 API 之前调用，否则在 125%/150% 缩放下
    /// 采集到的画面会被系统拉伸，坐标映射也会错位。
    /// </summary>
    public static void EnablePerMonitorV2()
    {
        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch
        {
            // 旧系统或不支持的场景下忽略
        }
    }
}

/// <summary>
/// 基于 GDI BitBlt + DIB Section 的屏幕采集。
/// 限制：无法采集独占全屏 / 硬件覆盖层内容；这类场景需要 DXGI Desktop Duplication 或 WGC。
/// </summary>
public sealed class DisplayCapture : IDisposable
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const int DI_NORMAL = 0x0003;
    private const uint CURSOR_SHOWING = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy,
                                          uint step, IntPtr hbr, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
                                                  out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                      IntPtr hdcSrc, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// 显示器热插拔 / 改模式时桌面表面正在重建，此时 BitBlt 和它撞上会让 GDI 直接崩掉
    /// （表现为整个进程 AccessViolation 退出），所以插拔和抓屏共用这把锁互斥。
    /// </summary>
    public static readonly object ScreenGate = new();

    private readonly ScreenRect _rect;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly IntPtr _windowHwnd;   // 非 0 表示这是"抓某个窗口"而不是抓屏幕区域
    private readonly Func<IntPtr, int, int, bool>? _renderer;   // 非空表示由外部合成画面（虚拟扩展屏）

    private IntPtr _screenDc;
    private readonly IntPtr[] _memDc = new IntPtr[2];
    private readonly IntPtr[] _bitmap = new IntPtr[2];
    private readonly IntPtr[] _oldBitmap = new IntPtr[2];
    private readonly IntPtr[] _bits = new IntPtr[2];
    private volatile bool _disposed;
    private readonly object _lifeLock = new();
    private bool _inCapture;
    private bool _released;

    public DisplayCapture(ScreenRect rect) : this(rect, IntPtr.Zero)
    {
    }

    /// <summary>抓某个窗口的客户区（hwnd 非 0 时走 PrintWindow，rect 只用来做光标/输入映射）。</summary>
    public DisplayCapture(ScreenRect rect, IntPtr windowHwnd)
    {
        _rect = rect;
        _windowHwnd = windowHwnd;
        // 编码器要求偶数宽高，采集侧先对齐，避免每帧做额外的裁剪
        _width = rect.Width % 2 == 0 ? rect.Width : rect.Width - 1;
        _height = rect.Height % 2 == 0 ? rect.Height : rect.Height - 1;
        _stride = _width * 4;
        CreateResources();
    }

    public bool IsWindow => _windowHwnd != IntPtr.Zero;
    public IntPtr WindowHandle => _windowHwnd;
    public bool IsRendered => _renderer != null;

    /// <summary>软件合成的画面（虚拟扩展屏）：每帧调用 renderer(memDc, w, h)。</summary>
    public DisplayCapture(ScreenRect rect, Func<IntPtr, int, int, bool> renderer) : this(rect, IntPtr.Zero)
    {
        _renderer = renderer;
    }

    public ScreenRect Rect => _rect;
    public int Width => _width;
    public int Height => _height;
    public int Stride => _stride;

    /// <summary>第 slot 块缓冲的像素首地址（双缓冲，slot 取 0/1）。</summary>
    public IntPtr BitsOf(int slot) => _bits[slot & 1];

    private void CreateResources()
    {
        _screenDc = GetDC(IntPtr.Zero);

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = _width,
            biHeight = -_height,      // 负数 = 自顶向下的像素布局，与 BGRA 视频帧一致
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,        // BI_RGB
            biSizeImage = (uint)(_stride * _height)
        };

        // 两块 DIB：采集线程写一块的同时，编码线程可以读另一块
        try
        {
            for (var i = 0; i < 2; i++)
            {
                _memDc[i] = CreateCompatibleDC(_screenDc);
                _bitmap[i] = CreateDIBSection(_screenDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
                if (_bitmap[i] == IntPtr.Zero || bits == IntPtr.Zero)
                {
                    throw new InvalidOperationException("创建 DIB Section 失败，无法采集画面");
                }
                _bits[i] = bits;
                _oldBitmap[i] = SelectObject(_memDc[i], _bitmap[i]);
            }
        }
        catch
        {
            // 半途失败也要把已经建好的 GDI 句柄还回去，否则每失败一次就漏一批句柄
            ReleaseResources();
            throw;
        }
    }

    /// <summary>抓取一帧到第 slot 块缓冲。返回 false 表示本次抓取失败（例如锁屏 / 窗口已关闭）。</summary>
    public bool Capture(int slot, bool drawCursor)
    {
        // 拔屏 / 停管线时，采集线程可能正卡在 BitBlt 里（虚拟屏被拔掉的瞬间能卡好几秒）。
        // 这时不能把 DIB 和 DXGI 复制器释放掉，否则那一帧会写进已释放内存，
        // 直接把托管堆写坏（表现是几秒后任意线程莫名 AccessViolation）。
        // 所以释放统一交给"最后一次抓屏结束时"去做。
        if (!BeginCapture()) return false;
        try
        {
            return CaptureCore(slot, drawCursor);
        }
        finally
        {
            EndCapture();
        }
    }

    private bool CaptureCore(int slot, bool drawCursor)
    {
        slot &= 1;

        if (_renderer != null)
        {
            var rendered = _renderer(_memDc[slot], _width, _height);
            if (rendered && drawCursor) DrawCursor(slot);
            return rendered;
        }

        if (_windowHwnd != IntPtr.Zero)
        {
            if (!WindowSource.CaptureTo(_windowHwnd, _memDc[slot], _width, _height)) return false;
            if (drawCursor) DrawCursor(slot);
            return true;
        }

        // 只用 SRCCOPY：加 CAPTUREBLT 会强制捕获分层窗口，单帧开销明显变大（实测掉帧近一半），
        // 代价是极少数全透明覆盖层可能抓不到，这里取性能优先。
        bool ok;
        lock (ScreenGate)
        {
            ok = BitBlt(_memDc[slot], 0, 0, _width, _height, _screenDc, _rect.X, _rect.Y, SRCCOPY);
        }
        if (drawCursor) DrawCursor(slot);
        return ok;
    }

    /// <summary>
    /// 重新获取屏幕 DC。显示器热插拔 / 显示模式切换之后，进程里缓存的那个屏幕 DC
    /// 可能指向一块已经失效的桌面表面（BitBlt 一直返回 false），重取一次即可恢复。
    /// </summary>
    public void RefreshScreenDc()
    {
        if (_disposed || _renderer != null || _windowHwnd != IntPtr.Zero) return;

        lock (ScreenGate)
        {
            try
            {
                if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            }
            catch
            {
            }
            _screenDc = GetDC(IntPtr.Zero);
        }
    }

    private void DrawCursor(int slot)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci)) return;
        if ((ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        DrawIconEx(_memDc[slot], ci.ptScreenPos.X - _rect.X, ci.ptScreenPos.Y - _rect.Y,
                   ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    public void Dispose()
    {
        bool release;
        lock (_lifeLock)
        {
            if (_disposed) return;
            _disposed = true;
            // 有抓屏正在进行就先不释放，等那一帧收尾时再放（见 Capture 的注释）
            release = !_inCapture && !_released;
            if (release) _released = true;
        }
        if (release) ReleaseResources();
    }

    private bool BeginCapture()
    {
        lock (_lifeLock)
        {
            if (_disposed) return false;
            _inCapture = true;
            return true;
        }
    }

    private void EndCapture()
    {
        bool release;
        lock (_lifeLock)
        {
            _inCapture = false;
            release = _disposed && !_released;
            if (release) _released = true;
        }
        if (release) ReleaseResources();
    }

    /// <summary>释放 GDI 句柄；重复调用安全（创建失败时也走这里）。</summary>
    private void ReleaseResources()
    {
        // 和 BitBlt / RefreshScreenDc 串行：否则可能刚删掉 DIB，
        // 抓屏线程就拿着那块内存做 BitBlt，直接踩坏内存
        lock (ScreenGate)
        {
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    if (_memDc[i] != IntPtr.Zero && _oldBitmap[i] != IntPtr.Zero)
                    {
                        SelectObject(_memDc[i], _oldBitmap[i]);
                    }
                    if (_bitmap[i] != IntPtr.Zero) DeleteObject(_bitmap[i]);
                    if (_memDc[i] != IntPtr.Zero) DeleteDC(_memDc[i]);
                }
                if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            }
            catch
            {
            }
            _screenDc = IntPtr.Zero;
            for (var i = 0; i < 2; i++) _memDc[i] = _oldBitmap[i] = _bitmap[i] = _bits[i] = IntPtr.Zero;
        }
    }

    // ---------------- 显示器枚举 ----------------

    public static List<ScreenRect> GetMonitors()
    {
        var list = new List<ScreenRect>();
        var proc = new MonitorEnumProc((hMonitor, hdc, lprc, data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                list.Add(new ScreenRect(mi.rcMonitor.Left, mi.rcMonitor.Top,
                                        mi.rcMonitor.Right - mi.rcMonitor.Left,
                                        mi.rcMonitor.Bottom - mi.rcMonitor.Top));
            }
            return true;
        });
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    public static ScreenRect GetVirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
        Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN)));

    /// <summary>按配置选择采集区域；序号越界时退化为主显示器。</summary>
    public static ScreenRect ResolveRect(int monitorIndex)
    {
        if (monitorIndex < 0) return GetVirtualScreen();
        var monitors = GetMonitors();
        if (monitors.Count == 0) return GetVirtualScreen();
        return monitorIndex < monitors.Count ? monitors[monitorIndex] : monitors[0];
    }
}
