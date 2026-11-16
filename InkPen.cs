using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>
/// Windows Ink 笔输入注入：通过 CreateSyntheticPointerDevice + InjectSyntheticPointerInput
/// 注入带压力的真实笔事件（Win10 1809+）。设备创建失败时 <see cref="Available"/> 为 false，
/// 调用方应回退到鼠标注入。
/// </summary>
public sealed class InkPen
{
    private const int PT_PEN = 3;
    private const int POINTER_FEEDBACK_DEFAULT = 1;

    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint PEN_MASK_TILT_X = 0x00000004;
    private const uint PEN_MASK_TILT_Y = 0x00000008;

    private const uint POINTER_FLAG_NEW = 0x00000001;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    private const uint POINTER_FLAG_PRIMARY = 0x00000200;
    private const uint POINTER_FLAG_CONFIDENCE = 0x00000400;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public int pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputState;
        public uint dwKeyStates;
        public ulong performanceCount;
        public int buttonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_PEN_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint penMask;
        public uint pressure;
        public uint rotation;
        public int tiltX;
        public int tiltY;
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct POINTER_TYPE_INFO
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public POINTER_PEN_INFO penInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, POINTER_TYPE_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern bool DestroySyntheticPointerDevice(IntPtr device);

    private readonly ConfigModel _cfg;
    private IntPtr _device;
    private bool _available;
    private uint _frameId;
    private bool _inContact;

    public InkPen(ConfigModel cfg)
    {
        _cfg = cfg;
        if (!cfg.EnableWindowsInk) return;
        try
        {
            _device = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
            _available = _device != IntPtr.Zero;
        }
        catch
        {
            _available = false;
        }
    }

    public bool Available => _available;

    public void PenDown(int x, int y, float pressure)
    {
        if (!_available) return;
        _inContact = true;
        Inject(x, y, pressure,
               POINTER_FLAG_NEW | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_DOWN |
               POINTER_FLAG_PRIMARY | POINTER_FLAG_CONFIDENCE | POINTER_FLAG_FIRSTBUTTON);
    }

    public void PenMove(int x, int y, float pressure)
    {
        if (!_available || !_inContact) return;
        Inject(x, y, pressure,
               POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_PRIMARY);
    }

    public void PenUp(int x, int y)
    {
        if (!_available || !_inContact) return;
        _inContact = false;
        Inject(x, y, 0f, POINTER_FLAG_UP | POINTER_FLAG_INRANGE | POINTER_FLAG_PRIMARY);
    }

    private void Inject(int x, int y, float pressure, uint flags)
    {
        var info = new POINTER_TYPE_INFO
        {
            type = PT_PEN,
            penInfo = new POINTER_PEN_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = PT_PEN,
                    pointerId = 1,
                    frameId = ++_frameId,
                    pointerFlags = flags,
                    hwndTarget = WindowFromPoint(new POINT { X = x, Y = y }),
                    ptPixelLocation = new POINT { X = x, Y = y },
                    ptPixelLocationRaw = new POINT { X = x, Y = y },
                    dwTime = unchecked((uint)Environment.TickCount64),
                    buttonChangeType = (flags & POINTER_FLAG_DOWN) != 0 ? 0 :
                                       (flags & POINTER_FLAG_UP) != 0 ? 2 : 1
                },
                penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y,
                pressure = (uint)Math.Clamp((int)(pressure * 1024), 0, 1024),
                rotation = 0,
                tiltX = 0,
                tiltY = 0
            }
        };

        try
        {
            InjectSyntheticPointerInput(_device, new[] { info }, 1);
        }
        catch
        {
            _available = false;
        }
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero)
        {
            try { DestroySyntheticPointerDevice(_device); } catch { }
            _device = IntPtr.Zero;
        }
        _available = false;
    }
}
