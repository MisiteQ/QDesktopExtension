using System.Runtime.InteropServices;

namespace QDesktopExtension;

// Win32 SendInput 需要的结构。同一份定义给鼠标和键盘共用，
// 避免多个文件各写一套导致 sizeof(INPUT) 不一致。
[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public int mouseData;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public INPUTUNION u;
}

public static class SendInputHelper
{
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static bool Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

    public static bool MouseMove(int nx, int ny) =>
        Send(new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = nx, dy = ny, dwFlags = 0x0001 | 0x8000 | 0x4000   // MOVE | ABSOLUTE | VIRTUALDESK
                    }
                }
            }
        });

    public static bool MouseEvent(int nx, int ny, uint extraFlags, int data = 0) =>
        Send(new[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = nx, dy = ny, mouseData = data,
                        dwFlags = extraFlags | 0x8000 | 0x4000
                    }
                }
            }
        });

    public static bool KeyEvent(uint vk, uint scan, uint extraFlags) =>
        Send(new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)scan, dwFlags = extraFlags }
                }
            }
        });

    /// <summary>扩展键（方向键 / Ins / Del / Home / End / PgUp / PgDn / 小键盘等）需要 EXTENDEDKEY 标志。</summary>
    public static bool NeedsExtendedKey(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x90 or 0x6F => true,
        _ => false
    };
}
