using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>
/// 远端输入注入：鼠标走 SendInput + 虚拟桌面绝对坐标（多屏正确），
/// 笔在 InkPen 可用时走真实笔压注入，否则回退鼠标。
/// </summary>
public class TouchForwarder
{
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private readonly InkPen _inkPen;
    private readonly ConfigModel _cfg;
    private int _screenX;
    private int _screenY;
    private int _screenW;
    private int _screenH;
    private VirtualScreen? _virtual;   // 非空时输入按虚拟扩展屏的格子换算
    private IntPtr _virtualTarget;

    /// <summary>虚拟屏输入映射的诊断日志（由 AppHost 注入）。</summary>
    public static Logger? DebugLog;

    public TouchForwarder(ConfigModel cfg, InkPen inkPen, ScreenRect rect)
    {
        _cfg = cfg;
        _inkPen = inkPen;
        SetBounds(rect);
    }

    public void SetBounds(ScreenRect rect)
    {
        _virtual = null;
        _screenX = rect.X;
        _screenY = rect.Y;
        _screenW = Math.Max(1, rect.Width);
        _screenH = Math.Max(1, rect.Height);
    }

    /// <summary>切到虚拟扩展屏：点了哪个格子就注入到哪个窗口。</summary>
    public void SetVirtualScreen(VirtualScreen? virtualScreen) => _virtual = virtualScreen;

    /// <summary>把归一化坐标换算成桌面坐标；虚拟屏上点在格子之间的空白处时返回 false（不注入）。</summary>
    private bool TryMap(float rx, float ry, out int x, out int y, bool activate = false)
    {
        if (_virtual != null)
        {
            if (_virtual.HitTest(rx, ry, out x, out y, out var hwnd, out _))
            {
                // 往下按之前先把目标窗口提到最前，否则点击会被盖在上面的窗口吃掉
                if (activate && hwnd != IntPtr.Zero && hwnd != _virtualTarget)
                {
                    DebugLog?.Info($"虚拟屏输入: ({rx:F3},{ry:F3}) → 窗口「{WindowSource.GetTitle(hwnd)}」(hwnd={hwnd.ToInt64()}) 坐标 ({x},{y})");
                    WindowSource.Activate(hwnd);
                    _virtualTarget = hwnd;
                }
                return true;
            }
            x = y = 0;
            return false;
        }
        x = _screenX + (int)Math.Round(Math.Clamp(rx, 0f, 1f) * (_screenW - 1));
        y = _screenY + (int)Math.Round(Math.Clamp(ry, 0f, 1f) * (_screenH - 1));
        return true;
    }

    private (int x, int y) Map(float rx, float ry)
    {
        TryMap(rx, ry, out var x, out var y);
        return (x, y);
    }

    /// <summary>把屏幕坐标转成 SendInput 需要的 0..65535 虚拟桌面坐标。</summary>
    private static (int nx, int ny) Normalize(int x, int y)
    {
        var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
        var vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var nx = (int)Math.Round((x - vx) * 65535.0 / Math.Max(1, vw - 1));
        var ny = (int)Math.Round((y - vy) * 65535.0 / Math.Max(1, vh - 1));
        return (Math.Clamp(nx, 0, 65535), Math.Clamp(ny, 0, 65535));
    }

    private static void Send(int x, int y, uint flags, int data = 0)
    {
        var (nx, ny) = Normalize(x, y);
        SendInputHelper.MouseEvent(nx, ny, flags, data);
    }

    public void Move(float rx, float ry)
    {
        if (!TryMap(rx, ry, out var x, out var y)) return;
        Send(x, y, MOUSEEVENTF_MOVE);
    }

    public void Down(float rx, float ry, string button)
    {
        if (!TryMap(rx, ry, out var x, out var y, true)) return;
        Send(x, y, ButtonFlag(button, down: true));
    }

    public void Up(float rx, float ry, string button)
    {
        if (!TryMap(rx, ry, out var x, out var y, true)) return;
        Send(x, y, ButtonFlag(button, down: false));
    }

    private static uint ButtonFlag(string button, bool down) => button switch
    {
        "right" => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
        "middle" => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
        _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
    };

    public void Wheel(float rx, float ry, int delta)
    {
        if (!TryMap(rx, ry, out var x, out var y, true)) return;
        // 带绝对坐标的滚轮事件会同时把光标定位到目标位置，滚轮才能作用在正确的窗口
        Send(x, y, MOUSEEVENTF_WHEEL, delta);
    }

    public void PenDown(float rx, float ry, float pressure)
    {
        if (!TryMap(rx, ry, out var x, out var y, true)) return;
        if (_cfg.EnableWindowsInk && _inkPen.Available)
        {
            _inkPen.PenDown(x, y, pressure);
        }
        else
        {
            Send(x, y, MOUSEEVENTF_MOVE);
            Send(x, y, MOUSEEVENTF_LEFTDOWN);
        }
    }

    public void PenMove(float rx, float ry, float pressure)
    {
        if (!TryMap(rx, ry, out var x, out var y)) return;
        if (_cfg.EnableWindowsInk && _inkPen.Available)
        {
            _inkPen.PenMove(x, y, pressure);
        }
        else
        {
            Send(x, y, MOUSEEVENTF_MOVE);
        }
    }

    public void PenUp(float rx, float ry)
    {
        if (!TryMap(rx, ry, out var x, out var y)) return;
        if (_cfg.EnableWindowsInk && _inkPen.Available)
        {
            _inkPen.PenUp(x, y);
        }
        else
        {
            Send(x, y, MOUSEEVENTF_LEFTUP);
        }
    }
}

