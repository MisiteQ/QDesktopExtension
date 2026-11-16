namespace QDesktopExtension;

/// <summary>采集源的三种形态：某台显示器（-1 = 全部）、某个窗口、软件合成的虚拟扩展屏。</summary>
public sealed record CaptureSource(bool IsWindow, int MonitorIndex, IntPtr Hwnd, string Title, bool Virtual,
                                   int VirtualWidth = 1920, int VirtualHeight = 1080)
{
    public static CaptureSource Monitor(int index) => new(false, index, IntPtr.Zero, "", false);

    public static CaptureSource Window(IntPtr hwnd, string title) =>
        new(true, 0, hwnd, title ?? "", false);

    /// <summary>虚拟扩展屏：把配置里选中的若干窗口合成到一块画布（不需要任何虚拟显示器驱动）。</summary>
    public static CaptureSource VirtualSource(int width = 1920, int height = 1080) =>
        new(false, 0, IntPtr.Zero, "", true, width, height);

    /// <summary>
    /// 按配置解析出实际生效的采集源：窗口句柄失效（程序关了/重启了）时，
    /// 用标题找回同名窗口；再找不到就退回显示器，别让服务起不来。
    /// </summary>
    public static CaptureSource FromConfig(ConfigModel cfg)
    {
        if (cfg.UseVirtualScreen)
            return VirtualSource(cfg.VirtualScreenWidth, cfg.VirtualScreenHeight);

        // 暂不提供"只显示某个窗口"作为独立采集源（功能收敛为复制屏幕 / 扩展屏两种），
        // 旧的 CaptureWindow 配置一律忽略，避免升级后卡在一个没有 UI 入口的模式里。
        // 显示器序号是当时记下来的，插过虚拟屏之后序号会漂移：越界或指到我们自己插的
        // 虚拟屏上时退回主屏，否则共享画面会去抓一块没人看的空屏。
        var index = cfg.CaptureMonitor;
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (index < 0 || index >= screens.Length || VirtualDisplayMode.IsParsecMonitor(index))
            index = Array.FindIndex(screens, s => s.Primary);
        return Monitor(index < 0 ? 0 : index);
    }

    /// <summary>创建采集器；窗口已关闭时抛 InvalidOperationException。</summary>
    public DisplayCapture CreateCapture(VirtualScreen? virtualScreen = null)
    {
        if (Virtual)
        {
            if (virtualScreen == null) throw new InvalidOperationException("虚拟扩展屏没有初始化");
            return new DisplayCapture(virtualScreen.Rect, virtualScreen.Render);
        }
        if (IsWindow)
        {
            var rect = WindowSource.GetClientScreenRect(Hwnd)
                       ?? throw new InvalidOperationException($"窗口已关闭或不可见：{Title}");
            return new DisplayCapture(rect, Hwnd);
        }
        return new DisplayCapture(DisplayCapture.ResolveRect(MonitorIndex));
    }

    /// <summary>采集区域（窗口/虚拟屏模式下随窗口移动、缩放变化，调用方要重新取）。</summary>
    public ScreenRect Rect => Virtual
        ? new ScreenRect(0, 0, VirtualWidth, VirtualHeight)
        : IsWindow
            ? WindowSource.GetClientScreenRect(Hwnd) ?? new ScreenRect(0, 0, 2, 2)
            : DisplayCapture.ResolveRect(MonitorIndex);

    public string Describe() => Virtual
        ? "虚拟扩展屏"
        : IsWindow
            ? $"窗口「{Title}」"
            : MonitorIndex < 0 ? "全部显示器" : $"显示器 {MonitorIndex}";

    public string ToJson()
    {
        var r = Rect;
        if (Virtual) return $"{{\"kind\":\"virtual\",\"w\":{r.Width},\"h\":{r.Height}}}";
        if (IsWindow)
            return $"{{\"kind\":\"window\",\"hwnd\":{Hwnd.ToInt64()},\"title\":{JsonText.Quote(Title)},\"w\":{r.Width},\"h\":{r.Height}}}";
        return $"{{\"kind\":\"monitor\",\"index\":{MonitorIndex},\"w\":{r.Width},\"h\":{r.Height}}}";
    }
}

internal static class JsonText
{
    /// <summary>把字符串转成合法的 JSON 字面量（带引号、转义反斜杠/引号/控制字符）。</summary>
    public static string Quote(string s) =>
        System.Text.Json.JsonSerializer.Serialize(s ?? "");
}


