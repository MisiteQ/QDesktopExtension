using System.Net.NetworkInformation;

namespace QDesktopExtension;

/// <summary>
/// 把"建采集 → 建编码 → 起服务 → 跑管线"整套生命周期收进一个可反复启停的对象，
/// 让界面上的"启动/停止/保存配置后重启"都走同一条路径。
/// </summary>
public sealed class AppHost
{
    private readonly string _cfgPath;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private volatile string? _lastError;
    private StreamStats? _stats;
    private StreamServer? _server;
    private UsbBridge? _usb;
    private ClientScreenManager? _clientScreens;

    /// <summary>USB 直连状态（给面板显示）。</summary>
    public UsbBridge? Usb => _usb;

    public AppHost(string cfgPath)
    {
        _cfgPath = cfgPath;
        Config = ConfigModel.LoadOrCreate(cfgPath);
    }

    public ConfigModel Config { get; private set; }

    /// <summary>服务停止时同步已保存配置，让地址展示使用最新端口。</summary>
    public void ApplySavedConfig(ConfigModel cfg)
    {
        if (!_running) Config = cfg;
    }

    public string ConfigPath => _cfgPath;
    public bool Running => _running;
    public string? LastError => _lastError;
    public StreamStats? Stats => _stats;
    public StreamServer? Server => _server;

    /// <summary>每台设备独立扩展屏的管理器（驱动不可用时为退化模式）。</summary>
    public ClientScreenManager? ClientScreens => _clientScreens;
    public int HttpPort => Config.HttpPort;

    /// <summary>启动服务。已在运行则忽略；失败信息写进 LastError。</summary>
    public void Start(ConfigModel? cfg = null)
    {
        if (_running) return;
        if (cfg != null) Config = cfg;
        _lastError = null;
        _running = true;
        _cts = new CancellationTokenSource();
        var config = Config;
        var token = _cts.Token;

        _thread = new Thread(() => RunLoop(config, token)) { IsBackground = true, Name = "stream-host" };
        _thread.Start();
    }

    /// <summary>只请求停止（可从 HTTP 线程调用），实际清理交给宿主线程。</summary>
    public void RequestStop()
    {
        try { _cts?.Cancel(); } catch { }
    }

    /// <summary>请求停止并等待线程收工；返回是否真的停下来了。</summary>
    public bool Stop(int timeoutMs = 8000)
    {
        RequestStop();
        var t = _thread;
        if (t != null && t.IsAlive && !t.Join(timeoutMs))
        {
            // 再给一轮缓冲时间：停不下来时不能直接起新服务，否则端口会撞车
            t.Join(Math.Min(timeoutMs, 4000));
        }
        var alive = t != null && t.IsAlive;
        if (!alive)
        {
            _thread = null;
            _running = false;
        }
        return !alive;
    }

    /// <summary>
    /// 换一份配置后重启：放到后台线程做，避免界面卡住；
    /// 旧服务没停下来时不强启新服务，把原因写进 LastError 由面板提示。
    /// </summary>
    public void Restart(ConfigModel cfg)
    {
        Config = cfg;
        Task.Run(() =>
        {
            if (!Stop(8000))
            {
                _lastError = "上一次服务没有及时停下来，重启已取消（可再点一次，或先点【停止服务】）";
                _running = false;
                return;
            }
            Start(cfg);
        });
    }

    private void RunLoop(ConfigModel cfg, CancellationToken token)
    {
        Logger? logger = null;
        DisplayCapture? capture = null;
        VideoEncoder? encoder = null;
        StreamServer? server = null;
        InkPen? inkPen = null;
        StreamPipeline? pipeline = null;
        try
        {
            logger = new Logger(cfg);
            logger.Info("QDesktopExtension 启动");
            logger.Info($"配置文件: {_cfgPath}");

            // 兜底记录：进程级未处理异常（含后台线程）至少要在日志里留下现场
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { logger!.Error($"未处理异常，进程即将退出: {e.ExceptionObject}"); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try { logger!.Warn($"后台任务未处理异常: {e.Exception?.GetBaseException().Message}"); } catch { }
                e.SetObserved();
            };

            // 采集源：显示器 / 某个窗口 / 虚拟扩展屏（窗口句柄失效时按标题找回，再不行退回显示器）
            var source = CaptureSource.FromConfig(cfg);
            var virtualScreen = source.Virtual ? VirtualScreen.Build(cfg, logger) : null;
            var rect = virtualScreen?.Rect ?? source.Rect;
            logger.Info($"采集源: {source.Describe()} 区域 {rect}");

            var stats = new StreamStats();
            _stats = stats;
            var acl = new AccessControl(cfg);
            var traffic = new TrafficStat();
            inkPen = new InkPen(cfg);
            var touch = new TouchForwarder(cfg, inkPen, rect);
            TouchForwarder.DebugLog = logger;
            if (virtualScreen != null) touch.SetVirtualScreen(virtualScreen);

            capture = source.CreateCapture(virtualScreen);
            encoder = new VideoEncoder(cfg);
            server = new StreamServer(cfg, logger, acl, traffic, touch, stats);
            _server = server;

            var target = InitialTarget(cfg, rect);
            encoder.Init(target.Width, target.Height, capture.Width, capture.Height, cfg.Fps, target.BitRate);

            server.UpdateVideoInfo(encoder);
            server.CurrentMonitor = source.IsWindow ? -2 : source.Virtual ? -3 : source.MonitorIndex;
            server.CurrentSource = source; 
           server.VirtualWindowCount = cfg.VirtualWindows.Count;

            var adaptive = new AdaptiveController(cfg, logger, capture.Width, capture.Height, target.BitRate);
            var pipelineTmp = new StreamPipeline(cfg, logger, capture, encoder, adaptive, new ServerPipelineHost(server), stats, touch,
                                              source, virtualScreen);
            pipeline = pipelineTmp;
            server.MonitorRequest = pipeline.RequestMonitor;
            server.SourceRequest = pipeline.RequestSource;
            server.VirtualAddRequest = (h, t) => pipeline.AddVirtualWindow(h, t);
            server.VirtualClearRequest = pipeline.ClearVirtualWindows;
            server.ViewportRequest = pipeline.OnClientViewport;

            // 每台设备各一块独立扩展屏（Parsec VDD）；驱动不可用时自动退回共享模式。
            // 必须在 server.Start() 之前建好：清理上次残留的虚拟屏要几秒，
            // 这段时间里一旦有客户端鉴权通过，就会和"拔屏"撞在一起，
            // 或者在管理器还没建好时接入而拿不到自己的屏（画面 0fps）。
            _clientScreens = new ClientScreenManager(cfg, logger, server, inkPen, token);
            // 客户端触摸/鼠标要按它自己那块屏换算，不能落到主屏上
            server.InputTargetResolver = _clientScreens.ResolveTouch;

            server.Start();

            // USB 数据线直连（adb reverse），和局域网可以同时存在
            var usb = new UsbBridge(cfg, logger);
            _usb = usb;
            usb.Start();

            LogEndpoints(cfg, logger);
            server.EnableLocalShutdown(() =>
            {
                logger.Info("收到本机 /shutdown，正在停止...");
                RequestStop();
            });

            pipeline.Run(token);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            // 带上完整堆栈：只有 Message 时，“Specified method is not supported”这类异常没法定位来源
            logger?.Error($"服务异常退出: {ex}");
        }
        finally
        {
            _running = false;
            try { _usb?.Stop(); } catch { }
            _usb = null;
            try { _clientScreens?.Dispose(); } catch { }
            _clientScreens = null;
            try { pipeline?.Dispose(); } catch { }
            try { server?.Dispose(); } catch { }
            try { encoder?.Dispose(); } catch { }
            try { capture?.Dispose(); } catch { }
            try { inkPen?.Dispose(); } catch { }
            _server = null;
            _stats = null;
            logger?.Info("已停止");
        }
    }

    /// <summary>本机可用的访问地址（用于面板上"打开网页"和复制链接）。</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
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

    private static void LogEndpoints(ConfigModel cfg, Logger logger)
    {
        logger.Info($"网页客户端: http://localhost:{cfg.HttpPort}/");
        foreach (var ip in LocalAddresses())
            logger.Info($"            http://{ip}:{cfg.HttpPort}/  (WebSocket :{cfg.WsPort})");
        logger.Info($"状态探针: http://localhost:{cfg.HttpPort}/status");
    }

    private static VideoTarget InitialTarget(ConfigModel cfg, ScreenRect rect)
    {
        // 宽高按同一系数缩放：超宽屏 / 竖屏直接压宽或压高会拉伸画面；上限 0 = 跟随显示器。
        // 同时压进 H.264 Level 4.0（SPS 写的是 4.0）：超过 1920x1080 手机硬解直接失败，
        // 客户端会在"建独立屏之前"先吃一口共享画面就报 H.264 解码失败
        var capW = Math.Min(cfg.MaxWidth > 0 ? cfg.MaxWidth : rect.Width, 1920);
        var capH = Math.Min(cfg.MaxHeight > 0 ? cfg.MaxHeight : rect.Height, 1080);
        var fit = Math.Min(1.0, Math.Min((double)capW / rect.Width, (double)capH / rect.Height));
        var w = Even(Math.Max(2, (int)Math.Round(rect.Width * fit)));
        var h = Even(Math.Max(2, (int)Math.Round(rect.Height * fit)));
        return new VideoTarget(w, h, cfg.BitRate);
    }

    private static int Even(int v) => v % 2 == 0 ? v : Math.Max(2, v - 1);
}

