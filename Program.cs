using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QDesktopExtension;

internal static class Program
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        // 最先挂崩溃兜底：越早越好，否则启动阶段的异常连日志都不会留下
        InstallCrashHandlers();

        // 默认系统定时器精度是 15.6ms，会让 Thread.Sleep 严重超出目标间隔，
        // 直接表现为帧率卡在 15~20fps 而不是配置的 30fps
        try { timeBeginPeriod(1); } catch { }

        // 必须在抓屏/建窗口之前完成，否则高 DPI 下采集尺寸与坐标都会错
        Dpi.EnablePerMonitorV2();

        var wantHelp = args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/?");
        var isSelfTest = args.Length > 0 &&
                         args[0].StartsWith("--selftest", StringComparison.OrdinalIgnoreCase);
        var isConsoleFlag = args.Length > 0 && args[0].Equals("--console", StringComparison.OrdinalIgnoreCase);
        var looksLikeConfigPath = args.Length > 0 &&
                                  !args[0].StartsWith("-", StringComparison.Ordinal) &&
                                  args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        // 旧的命令行用法（调试 / 自检 / 脚本）保留；默认双击图标走图形界面
        if (wantHelp || isSelfTest || isConsoleFlag || looksLikeConfigPath)
        {
            if (!isSelfTest) try { AllocConsole(); } catch { }
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            if (wantHelp) { PrintUsage(); return 0; }

            var path = (looksLikeConfigPath || isSelfTest) ? args[0] : string.Empty;
            var cfgPath = isSelfTest
                ? Path.Combine(AppContext.BaseDirectory, "appconfig.json")
                : (path.Length > 0 ? Path.GetFullPath(path) : Path.Combine(AppContext.BaseDirectory, "appconfig.json"));

            if (isSelfTest)
            {
                ConfigModel stCfg;
                try { stCfg = ConfigModel.LoadOrCreate(cfgPath); }
                catch (Exception ex) { Console.Error.WriteLine($"读取配置失败: {ex.Message}"); return 1; }
                var logger = new Logger(stCfg);
                return SelfTest.Run(stCfg, logger, args);
            }

            return RunConsole(cfgPath);
        }

        return RunGui();
    }

    // ---------- 崩溃兜底 ----------
    private static void InstallCrashHandlers()
    {
        // 后台线程 / 非 UI 线程抛出的未处理异常
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // WinForms 消息循环里的异常：默认会直接弹"系统错误"并结束进程，
        // 改成落到 crash 日志里，至少能看出是哪个调用点出的问题
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => WriteCrashLog("Application.ThreadException", e.Exception);
        }
        catch
        {
        }
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能反过来影响进程
        }
    }

    // ---------- 图形界面（双击图标） ----------
    private static int RunGui()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, "QDesktopExtension.Gui.Instance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("QDesktopExtension 已经在运行。\n\n请在任务栏右下角的托盘里双击图标打开设置面板。",
                "已有一个实例在运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        var cfgPath = Path.Combine(AppContext.BaseDirectory, "appconfig.json");
        AppHost host;
        try
        {
            host = new AppHost(cfgPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取配置失败：{ex.Message}\n\n配置文件：{cfgPath}",
                "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        Application.Run(new TrayApp(host));
        try { timeEndPeriod(1); } catch { }
        return 0;
    }

    // ---------- 控制台模式（--console / 传配置文件路径） ----------
    private static int RunConsole(string cfgPath)
    {
        ConfigModel cfg;
        try
        {
            cfg = ConfigModel.LoadOrCreate(cfgPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"读取配置失败: {ex.Message}");
            return 1;
        }

        var logger = new Logger(cfg);
        logger.Info("QDesktopExtension 启动（控制台模式）");
        logger.Info($"配置文件: {cfgPath}");

        var monitors = DisplayCapture.GetMonitors();
        logger.Info($"检测到 {monitors.Count} 个显示器: {string.Join(" | ", monitors)}");

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
            logger.Error($"初始化采集失败: {ex.Message}");
            return 2;
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
            logger.Error($"初始化编码器失败: {ex.Message}");
            logger.Error("请把 FFmpeg 动态库（avcodec-62 / avformat-62 / avutil-60 / swscale-9 等）放到程序目录的 ffmpeg 子目录下。");
            capture.Dispose();
            server.Dispose();
            return 3;
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

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            logger.Error($"服务启动失败: {ex.Message}");
            encoder.Dispose();
            capture.Dispose();
            return 4;
        }

        PrintEndpoints(cfg, logger);

        using var cts = new CancellationTokenSource();
        ClientScreenManager? clientScreens = null;
        server.EnableLocalShutdown(() =>
        {
            logger.Info("收到本机 /shutdown，正在停止...");
            cts.Cancel();
        });

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.Info("收到退出信号，正在停止...");
            cts.Cancel();
        };

        logger.Info("开始推流，按 Ctrl+C 退出");
        try
        {
            // 控制台模式也要支持每台 H.264 客户端一块独立扩展屏，不能只在 GUI 模式接线
            clientScreens = new ClientScreenManager(cfg, logger, server, inkPen, cts.Token);
            pipeline.Run(cts.Token);
        }
        catch (Exception ex)
        {
            logger.Error($"推流异常退出: {ex}");
        }
        finally
        {
            try { clientScreens?.Dispose(); } catch { }
            pipeline.Dispose();
            server.Dispose();
            encoder.Dispose();
            capture.Dispose();
            inkPen.Dispose();
            try { timeEndPeriod(1); } catch { }
            logger.Info("已退出");
        }

        return 0;
    }

    private static VideoTarget InitialTarget(ConfigModel cfg, ScreenRect rect)
    {
        // 宽高按同一系数缩放：超宽屏 / 竖屏直接压宽或压高会拉伸画面；上限 0 = 跟随显示器。
        // 同时压进 H.264 Level 4.0（SPS 里写的就是 4.0）：超过 1920x1080 是非法的
        var capW = Math.Min(cfg.MaxWidth > 0 ? cfg.MaxWidth : rect.Width, 1920);
        var capH = Math.Min(cfg.MaxHeight > 0 ? cfg.MaxHeight : rect.Height, 1080);
        var fit = Math.Min(1.0, Math.Min((double)capW / rect.Width, (double)capH / rect.Height));
        var w = Even(Math.Max(2, (int)Math.Round(rect.Width * fit)));
        var h = Even(Math.Max(2, (int)Math.Round(rect.Height * fit)));
        return new VideoTarget(w, h, cfg.BitRate);
    }

    private static int Even(int v) => v % 2 == 0 ? v : Math.Max(2, v - 1);

    private static void PrintEndpoints(ConfigModel cfg, Logger logger)
    {
        var addresses = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        addresses.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch
        {
        }

        logger.Info($"网页客户端: http://localhost:{cfg.HttpPort}/");
        foreach (var ip in addresses.Distinct())
        {
            logger.Info($"            http://{ip}:{cfg.HttpPort}/  (WebSocket :{cfg.WsPort})");
        }
        logger.Info($"状态探针: http://localhost:{cfg.HttpPort}/status");
        logger.Info($"本机停机: curl \"http://localhost:{cfg.HttpPort}/shutdown?token=<密码>\"");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
QDesktopExtension — Windows 屏幕串流服务

用法：
  QDesktopExtension.exe
      双击启动：托盘常驻 + 设置面板（推荐，服务会自动开始串流）

  QDesktopExtension.exe --console
      控制台模式，等价于以前 START.bat 的行为

  QDesktopExtension.exe <配置文件.json>
      用指定配置以控制台模式运行

  QDesktopExtension.exe --selftest [输出.h264]
      只跑合成图案的采集/编码自检，不启动服务

  QDesktopExtension.exe --help
      显示本帮助

退出：
  · 图形界面：面板里点【停止服务】再关窗口，或托盘菜单【退出】
  · 控制台模式：Ctrl+C
  · 或 GET http://localhost:<HttpPort>/shutdown?token=<Password>
""");
    }
}

