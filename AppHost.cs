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
    private Logger? _runLogger;

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
            _runLogger = logger;
            logger.Info("QDesktopExtension 启动");
            logger.Info($"配置文件: {_cfgPath}");

            // 兜底记录：进程级未处理异常（含后台线程）至少要在日志里留下现场。
            // 保存委托引用，停止时退订，避免反复启停累积订阅。
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 用共享工厂组装整套管线（采集 → 编码 → 服务 → 回调接线），避免与控制台模式重复实现
            var bundle = PipelineFactory.BuildPipeline(cfg, logger);
            capture = bundle.Capture;
            encoder = bundle.Encoder;
            server = bundle.Server;
            inkPen = bundle.InkPen;
            pipeline = bundle.Pipeline;
            _stats = bundle.Stats;
            _server = server;

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

            PipelineFactory.LogEndpoints(cfg, logger);
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
            // 退订进程级异常处理器，避免反复启停累积订阅
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
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
            _runLogger = null;
            logger?.Info("已停止");
            try { logger?.Dispose(); } catch { }
        }
    }

    /// <summary>进程级未处理异常兜底记录。</summary>
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        try { _runLogger?.Error($"未处理异常，进程即将退出: {e.ExceptionObject}"); } catch { }
    }

    /// <summary>后台任务未处理异常兜底记录，标记为已观察避免进程崩溃。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { _runLogger?.Warn($"后台任务未处理异常: {e.Exception?.GetBaseException().Message}"); } catch { }
        e.SetObserved();
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
}

