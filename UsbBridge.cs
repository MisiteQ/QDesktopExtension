using System.Diagnostics;

namespace QDesktopExtension;

/// <summary>
/// USB 数据线直连：用 adb reverse 把手机的 127.0.0.1:端口 转发到电脑，
/// 手机浏览器打开 http://127.0.0.1:8767/ 即可，流量走数据线而不是 Wi-Fi
/// （比无线稳、抖动小，码率和帧率都能开更高）。
/// 只依赖手机开启「USB 调试」并授权，不需要在手机上装任何 App。
/// </summary>
public sealed class UsbBridge
{
    private readonly ConfigModel _cfg;
    private readonly Logger _logger;
    private volatile bool _running;
    private Thread? _thread;

    /// <summary>给界面看的当前状态（"未检测到…" / "已连接: 序列号"）。</summary>
    public volatile string Status = "未启用";
    /// <summary>已建立转发的设备序列号列表。</summary>
    public volatile int DeviceCount;

    public UsbBridge(ConfigModel cfg, Logger logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public bool Enabled => _cfg.LinkMode is LinkMode.Usb or LinkMode.Both;

    public void Start()
    {
        if (!Enabled || _running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "usb-bridge" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _thread?.Join(1500); } catch { }
        _thread = null;
    }

    /// <summary>界面上的"重新检测"：重跑一遍检测与端口转发。</summary>
    public void Refresh()
    {
        if (!Enabled) { Status = "当前设置里没有启用 USB 连接"; return; }
        Stop();
        Status = "正在检测…";
        Start();
    }

    private void Loop()
    {
        var lastSerial = "";
        while (_running)
        {
            try
            {
                var adb = ResolveAdb();
                if (adb == null)
                {
                    Status = "没找到 adb（需要 Android 平台工具，或开启 USB 调试后由系统提供）";
                    SleepWhileRunning(8000);
                    continue;
                }

                var devices = ListDevices(adb);
                if (devices.Count == 0)
                {
                    Status = "未检测到已授权的 USB 设备（手机需开启 USB 调试并允许本机调试）";
                    DeviceCount = 0;
                    if (lastSerial.Length > 0) _logger.Info("USB 设备已断开");
                    lastSerial = "";
                    SleepWhileRunning(4000);
                    continue;
                }

                foreach (var serial in devices)
                {
                    // 两个端口都转发：8767 是网页，8765 是视频/输入通道
                    Run(adb, $"-s {serial} reverse tcp:{_cfg.HttpPort} tcp:{_cfg.HttpPort}");
                    Run(adb, $"-s {serial} reverse tcp:{_cfg.WsPort} tcp:{_cfg.WsPort}");
                }

                DeviceCount = devices.Count;
                var joined = string.Join("、", devices);
                Status = $"USB 直连已就绪：{joined}　手机浏览器打开 http://127.0.0.1:{_cfg.HttpPort}/";
                if (joined != lastSerial)
                {
                    _logger.Info($"USB 直连就绪（{joined}），已转发端口 {_cfg.HttpPort}/{_cfg.WsPort}");
                    lastSerial = joined;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"usb-bridge: {ex.Message}");
                Status = "USB 检测出错：" + ex.Message;
            }
            SleepWhileRunning(4000);
        }
        Status = "未启用";
    }

    /// <summary>按顺序找 adb：配置里指定 → PATH → 常见安装位置。</summary>
    private string? ResolveAdb()
    {
        if (!string.IsNullOrWhiteSpace(_cfg.AdbPath) && File.Exists(_cfg.AdbPath)) return _cfg.AdbPath;

        foreach (var p in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Android\Sdk\platform-tools\adb.exe"),
                     @"C:\Program Files\Android_Dex_Windows\Build_copy\adb-windows\adb.exe",
                     @"C:\platform-tools\adb.exe"
                 })
        {
            if (File.Exists(p)) return p;
        }

        // PATH 里的 adb
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (dir.Trim().Length == 0) continue;
            try
            {
                var cand = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }
        return null;
    }

    private List<string> ListDevices(string adb)
    {
        var text = Run(adb, "devices");
        var list = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("List of devices")) continue;
            var parts = t.Split('\t');
            if (parts.Length >= 2 && parts[1].Trim().StartsWith("device")) list.Add(parts[0].Trim());
        }
        return list;
    }

    private string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";

            // stdout/stderr 都要读走：只读一路时另一路写满管道缓冲区会把进程卡死
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync().ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

            if (!p.WaitForExit(3000))
            {
                try { p.Kill(true); } catch { }
                p.WaitForExit(1000);
            }

            // 进程已退出（或被强杀）后才收尾读取，不会再无限等待
            return stdout.Wait(2000) ? stdout.Result : "";
        }
        catch (Exception ex)
        {
            _logger.Debug($"adb 调用失败: {ex.Message}");
            return "";
        }
    }

    /// <summary>可被 Stop() 打断的等待，避免退出时要干等满一个检测周期。</summary>
    private void SleepWhileRunning(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (_running && Environment.TickCount64 < until) Thread.Sleep(100);
    }
}
