namespace QDesktopExtension;

/// <summary>
/// 一套构建完成的推流管线组件。控制台模式与 AppHost 复用同一套组装逻辑，
/// 避免"建采集 → 建编码 → 起服务 → 接回调"在两处重复实现、将来改一处漏一处。
/// 这里只做数据持有，不强制清理语义：各调用方的清理顺序/错误路径不同，仍由调用方各自处理。
/// </summary>
public sealed class PipelineBundle
{
    public required ConfigModel Config { get; init; }
    public required Logger Logger { get; init; }
    public required CaptureSource Source { get; init; }
    public VirtualScreen? VirtualScreen { get; init; }
    public required StreamStats Stats { get; init; }
    public required AccessControl Acl { get; init; }
    public required TrafficStat Traffic { get; init; }
    public required InkPen InkPen { get; init; }
    public required TouchForwarder Touch { get; init; }
    public required DisplayCapture Capture { get; init; }
    public required VideoEncoder Encoder { get; init; }
    public required StreamServer Server { get; init; }
    public required AdaptiveController Adaptive { get; init; }
    public required StreamPipeline Pipeline { get; init; }
}

/// <summary>
/// 共享的管线构建工厂：把"建采集 → 建编码 → 起服务 → 接回调"这套组装逻辑收在一处，
/// 供控制台模式（<see cref="Program.RunConsole"/>）与常驻宿主（<see cref="AppHost.RunLoop"/>）复用。
/// </summary>
public static class PipelineFactory
{
    /// <summary>
    /// 按配置构建整套推流管线（含采集、编码、服务、管线及回调接线）。
    /// 构建过程中任一步失败都会清理已创建的资源并抛出异常，绝不泄漏。
    /// </summary>
    /// <param name="cfg">已 Normalize 的配置。</param>
    /// <param name="logger">日志器。</param>
    public static PipelineBundle BuildPipeline(ConfigModel cfg, Logger logger)
    {
        // 采集源：显示器 / 某个窗口 / 虚拟扩展屏（窗口句柄失效时按标题找回，再不行退回显示器）
        var source = CaptureSource.FromConfig(cfg);
        var virtualScreen = source.Virtual ? VirtualScreen.Build(cfg, logger) : null;
        var rect = virtualScreen?.Rect ?? source.Rect;
        logger.Info($"采集源: {source.Describe()} 区域 {rect}");

        var stats = new StreamStats();
        var acl = new AccessControl(cfg);
        var traffic = new TrafficStat();
        var inkPen = new InkPen(cfg);
        if (cfg.EnableWindowsInk)
        {
            logger.Info(inkPen.Available
                ? "笔压注入已启用（InjectSyntheticPointerInput）"
                : "笔压注入不可用，自动回退为鼠标注入");
        }

        var touch = new TouchForwarder(cfg, inkPen, rect);
        TouchForwarder.DebugLog = logger;
        if (virtualScreen != null) touch.SetVirtualScreen(virtualScreen);

        DisplayCapture capture;
        try
        {
            capture = source.CreateCapture(virtualScreen);
        }
        catch (Exception ex)
        {
            try { inkPen.Dispose(); } catch { }
            throw new InvalidOperationException($"初始化采集失败: {ex.Message}", ex);
        }

        var encoder = new VideoEncoder(cfg);
        var server = new StreamServer(cfg, logger, acl, traffic, touch, stats);

        var target = InitialTarget(cfg, rect);
        try
        {
            encoder.Init(target.Width, target.Height, capture.Width, capture.Height, cfg.Fps, target.BitRate);
        }
        catch (Exception ex)
        {
            // 带上 FFmpeg 库缺失的排查提示：只有 "Specified method is not supported" 这类消息时用户无从下手
            try { capture.Dispose(); server.Dispose(); encoder.Dispose(); inkPen.Dispose(); } catch { }
            throw new InvalidOperationException(
                $"初始化编码器失败: {ex.Message}\n" +
                "请把 FFmpeg 动态库（avcodec-62 / avformat-62 / avutil-60 / swscale-9 等）放到程序目录的 ffmpeg 子目录下。", ex);
        }

        server.UpdateVideoInfo(encoder);
        server.CurrentMonitor = source.IsWindow ? -2 : source.Virtual ? -3 : source.MonitorIndex;
        server.CurrentSource = source;
        server.VirtualWindowCount = cfg.VirtualWindows.Count;

        var adaptive = new AdaptiveController(cfg, logger, capture.Width, capture.Height, target.BitRate);
        var pipeline = new StreamPipeline(cfg, logger, capture, encoder, adaptive, new ServerPipelineHost(server), stats, touch,
                                          source, virtualScreen);
        server.MonitorRequest = pipeline.RequestMonitor;
        server.SourceRequest = pipeline.RequestSource;
        server.VirtualAddRequest = (h, t) => pipeline.AddVirtualWindow(h, t);
        server.VirtualClearRequest = pipeline.ClearVirtualWindows;
        server.ViewportRequest = pipeline.OnClientViewport;

        return new PipelineBundle
        {
            Config = cfg,
            Logger = logger,
            Source = source,
            VirtualScreen = virtualScreen,
            Stats = stats,
            Acl = acl,
            Traffic = traffic,
            InkPen = inkPen,
            Touch = touch,
            Capture = capture,
            Encoder = encoder,
            Server = server,
            Adaptive = adaptive,
            Pipeline = pipeline,
        };
    }

    /// <summary>
    /// 本机可用的访问地址（用于面板上"打开网页"和复制链接）。
    /// </summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        list.Add(addr.Address.ToString());
                }
            }
        }
        catch { }
        return list.Distinct().ToList();
    }

    /// <summary>打印本机访问端点（网页客户端、状态探针）。</summary>
    public static void LogEndpoints(ConfigModel cfg, Logger logger)
    {
        logger.Info($"网页客户端: http://localhost:{cfg.HttpPort}/");
        foreach (var ip in LocalAddresses())
            logger.Info($"            http://{ip}:{cfg.HttpPort}/  (WebSocket :{cfg.WsPort})");
        logger.Info($"状态探针: http://localhost:{cfg.HttpPort}/status");
        logger.Info($"本机停机: curl \"http://localhost:{cfg.HttpPort}/shutdown?token=<密码>\"");
    }

    /// <summary>
    /// 计算编码目标尺寸。宽高按同一系数缩放：超宽屏 / 竖屏直接压宽或压高会拉伸画面；上限 0 = 跟随显示器。
    /// 同时压进 H.264 Level 4.0（SPS 写的是 4.0）：超过 1920x1080 手机硬解直接失败。
    /// </summary>
    public static VideoTarget InitialTarget(ConfigModel cfg, ScreenRect rect)
    {
        var capW = Math.Min(cfg.MaxWidth > 0 ? cfg.MaxWidth : rect.Width, 1920);
        var capH = Math.Min(cfg.MaxHeight > 0 ? cfg.MaxHeight : rect.Height, 1080);
        var fit = Math.Min(1.0, Math.Min((double)capW / rect.Width, (double)capH / rect.Height));
        var w = Even(Math.Max(2, (int)Math.Round(rect.Width * fit)));
        var h = Even(Math.Max(2, (int)Math.Round(rect.Height * fit)));
        return new VideoTarget(w, h, cfg.BitRate);
    }

    private static int Even(int v) => v % 2 == 0 ? v : Math.Max(2, v - 1);
}
