using Fleck;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace QDesktopExtension;

/// <summary>
/// 视频分发与输入通道：
/// - HTTP: 内置网页客户端 + /status
/// - WS: 文本通道走鉴权/输入/统计，二进制通道走视频帧（首字节 1=关键帧）
/// - 端口绑定：连接方式选「USB 数据线」时只监听回环，其余模式按 BindAddress 监听
/// </summary>
public sealed class StreamServer : IDisposable
{
    private sealed class ClientState
    {
        public bool Authed;
        public bool Jpeg;                 // true = 走 JPEG 静态帧（不支持 WebCodecs 的客户端）
        public bool Mse;                  // true = 走 fMP4 + MediaSource（支持 MSE 但没有 WebCodecs 的客户端）
        public bool OwnPipeline;          // true = 该客户端由自己的独立管线服务，不参与全局广播
        public string Ip = "";            // 断开时判断同一 IP 是否还有别的连接
        public int FailedAttempts;
        public long ConnectedAt = Environment.TickCount64;
        public long LastAuthOkAt;         // 上次下发 auth_ok 的时间（防止 ping 触发无限重发）
        public int InputCount;
        public long InputWindowStart = Environment.TickCount64;
        public int ViewportW;             // 客户端上报的屏幕尺寸（独立屏建好后补应用）
        public int ViewportH;
        public readonly HashSet<int> PressedKeys = new();
    }

    private readonly ConfigModel _cfg;
    private readonly Logger _logger;
    private readonly AccessControl _acl;
    private readonly TrafficStat _traffic;
    private readonly TouchForwarder _touch;
    private readonly KeyboardInjector _keyboard = new();
    private readonly StreamStats _stats;
    private readonly SimpleHttpServer _http;
    private readonly string _bind;

    private readonly Dictionary<IWebSocketConnection, ClientState> _clients = new();
    private readonly object _lock = new();

    // 跨连接累计的鉴权失败：单连接 5 次的上限可以被"断开重连"绕过，这里按 IP 封禁
    private readonly Dictionary<string, AuthFail> _authFails = new();

    private sealed class AuthFail
    {
        public int Count;
        public long WindowStartTicks;
        public long BanUntilTicks;
    }
    private readonly object _videoLock = new();
    private WebSocketServer? _ws;
    private Thread? _watchdog;
    private bool _running;
    private bool _lastLocked;

    private string _codecString = "avc1.42001E";
    private string _descriptionBase64 = "";
    private int _videoW = 1280;
    private int _videoH = 720;
    private byte[]? _lastKeyPacket;
    private byte[]? _mseInit;           // 当前 fMP4 init segment（moov），MSE 客户端必须先拿到它
    private int _mseGeneration;         // MSE 客户端接入代次，供管线判断是否需要重开封装器
    private bool _keyFrameRequested = true;

    public StreamServer(ConfigModel cfg, Logger logger, AccessControl acl, TrafficStat traffic,
                        TouchForwarder touch, StreamStats stats)
    {
        _cfg = cfg;
        _logger = logger;
        _acl = acl;
        _traffic = traffic;
        _touch = touch;
        _stats = stats;

        var html = LoadIndexHtml()
            .Replace("__WS_PORT__", cfg.WsPort.ToString())
            .Replace("__HTTP_PORT__", cfg.HttpPort.ToString())
            .Replace("__ALLOW_INPUT__", cfg.AllowRemoteInput ? "true" : "false");

        // "USB 数据线"模式下只监听回环：手机经 adb reverse 进来，局域网其他设备连不上，
        // 这样界面上选的连接方式才真的生效（而不是只决定要不要跑 adb）。
        _bind = cfg.LinkMode == LinkMode.Usb ? "127.0.0.1" : cfg.BindAddress;
        _http = new SimpleHttpServer(_bind, cfg.HttpPort, logger, html);
        _http.StatusProvider = BuildStatusJson;
        _http.WindowsProvider = BuildWindowsJson;
        _http.AccessToken = cfg.Password;
    }

    public int AuthedClients
    {
        get
        {
            lock (_lock) return _clients.Values.Count(c => c.Authed);
        }
    }

    /// <summary>还在吃"共享画面"的客户端数：都拿到独立扩展屏时共享管线就没人要了。</summary>
    public int SharedClients
    {
        get
        {
            lock (_lock) return _clients.Count(kv => kv.Value.Authed && !kv.Value.OwnPipeline && kv.Key.IsAvailable);
        }
    }

    /// <summary>共享画面里还在等 H.264/MSE 的客户端数（其余是纯 JPEG 客户端）。</summary>
    public int SharedVideoClients
    {
        get
        {
            lock (_lock) return _clients.Count(kv => kv.Value.Authed && !kv.Value.OwnPipeline
                                                       && !kv.Value.Jpeg && kv.Key.IsAvailable);
        }
    }

    private static string LoadIndexHtml()
    {
        try
        {
            var asm = typeof(StreamServer).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
            if (name == null) return "<h1>index.html 未打包进程序集</h1>";
            using var s = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(s, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "<h1>读取内置页面失败</h1>";
        }
    }

    public void Start()
    {
        _http.Start();

        var location = $"ws://{_bind}:{_cfg.WsPort}";
        _ws = new WebSocketServer(location);
        _ws.RestartAfterListenError = true;
        _ws.Start(OnConnection);
        _logger.Info($"WebSocket 监听 {location}");

        _running = true;
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "ws-watchdog" };
        _watchdog.Start();
    }

    private void OnConnection(IWebSocketConnection conn)
    {
        conn.OnOpen = () =>
        {
            var ip = conn.ConnectionInfo.ClientIpAddress;
            if (!_acl.IsIpAllowed(ip))
            {
                _logger.Warn($"拒绝连接（名单规则）: {ip}");
                try { conn.Close(); } catch { }
                return;
            }
            lock (_lock) _clients[conn] = new ClientState { Ip = ip };
            _logger.Info($"客户端接入: {ip}（未鉴权）");
        };

        conn.OnClose = () =>
        {
            var ip = conn.ConnectionInfo.ClientIpAddress;
            ClientState? state;
            lock (_lock)
            {
                // Fleck 在异常关闭时可能多次回调 OnClose，只在第一次做清理
                if (!_clients.TryGetValue(conn, out state)) return;
                _clients.Remove(conn);
            }
            // 客户端断线时把还按着的键松开，否则服务端会残留 Shift/Ctrl 这类按下状态
            foreach (var vk in state.PressedKeys) _keyboard.Key(vk, false);
            try { ClientClosedEvent?.Invoke(conn); } catch { }
            // 同一 IP 可能同时有多个标签页/设备，最后一个断开时才清限流状态
            bool stillOthers;
            lock (_lock) stillOthers = _clients.Values.Any(c => string.Equals(c.Ip, ip, StringComparison.OrdinalIgnoreCase));
            if (!stillOthers) _acl.Forget(ip);
            _logger.Info($"客户端断开: {ip}");
        };

        conn.OnError = ex => _logger.Warn($"连接异常 {conn.ConnectionInfo.ClientIpAddress}: {ex.Message}");

        conn.OnMessage = msg => HandleText(conn, msg);

        conn.OnBinary = bytes => _logger.Debug($"忽略客户端二进制消息 {bytes.Length}B");
    }

    private void HandleText(IWebSocketConnection conn, string msg)
    {
        ClientState? state;
        lock (_lock) state = _clients.TryGetValue(conn, out var s) ? s : null;
        if (state == null) return;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(msg);
        }
        catch
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "auth")
            {
                HandleAuth(conn, state, root);
                return;
            }

            // 客户端侧诊断信息（手机上开不了控制台，靠这条回传到服务端日志）
            if (type == "log")
            {
                var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                if (text.Length > 300) text = text[..300];
                _logger.Info($"客户端日志 {conn.ConnectionInfo.ClientIpAddress}: {text}");
                return;
            }

            if (!state.Authed) return;

            if (type == "input")
            {
                HandleInput(conn, state, root);
                return;
            }

            if (type == "monitor")
            {
                var index = root.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
                _logger.Info($"客户端请求切换显示器: {index}（来自 {conn.ConnectionInfo.ClientIpAddress}）");
                MonitorRequest?.Invoke(index);
                return;
            }

            // 新的采集源协议：{"type":"source","kind":"monitor|window","index":n,"hwnd":h,"title":"..."}
            if (type == "source")
            {
                var kind = root.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
                if (kind == "window")
                {
                    var hwnd = root.TryGetProperty("hwnd", out var h) && h.TryGetInt64(out var hv) ? hv : 0;
                    var title = root.TryGetProperty("title", out var tt) ? (tt.GetString() ?? "") : "";
                    if (hwnd != 0)
                    {
                        var target = new IntPtr(hwnd);
                        var name = title.Length > 0 ? title : WindowSource.GetTitle(target);
                        _logger.Info($"客户端请求采集窗口: 「{name}」(hwnd={hwnd})，来自 {conn.ConnectionInfo.ClientIpAddress}");
                        SourceRequest?.Invoke(CaptureSource.Window(target, name));
                        conn.Send($"{{\"type\":\"source\",\"source\":{CurrentSource.ToJson()},\"virtual\":{VirtualWindowCount},"
                                  + $"\"windows\":{BuildWindowsJson()}}}");
                        return;
                    }
                }
                else if (kind == "virtual")
                {
                    _logger.Info($"客户端请求切换到虚拟扩展屏（来自 {conn.ConnectionInfo.ClientIpAddress}）");
                    SourceRequest?.Invoke(CaptureSource.VirtualSource());
                    conn.Send($"{{\"type\":\"source\",\"source\":{CurrentSource.ToJson()},\"virtual\":{VirtualWindowCount},"
                              + $"\"windows\":{BuildWindowsJson()}}}");
                    return;
                }
                else
                {
                    var index = root.TryGetProperty("index", out var i2) && i2.TryGetInt32(out var n2) ? n2 : 0;
                    _logger.Info($"客户端请求采集显示器: {index}");
                    SourceRequest?.Invoke(CaptureSource.Monitor(index));
                    conn.Send($"{{\"type\":\"source\",\"source\":{CurrentSource.ToJson()},\"virtual\":{VirtualWindowCount},"
                              + $"\"windows\":{BuildWindowsJson()}}}");
                    return;
                }
            }

            if (type == "windows")
            {
                conn.Send($"{{\"type\":\"windows\",\"current\":{CurrentSource.ToJson()},"
                          + $"\"list\":{BuildWindowsJson()}}}");
                return;
            }

            // 客户端上报自己的屏幕尺寸：这块"新显示器"的分辨率就该等于对面那块屏
            if (type == "viewport")
            {
                var vw = root.TryGetProperty("w", out var pw) && pw.TryGetInt32(out var iw) ? iw : 0;
                var vh = root.TryGetProperty("h", out var ph) && ph.TryGetInt32(out var ih) ? ih : 0;
                // fill=false：客户端切回"完整"模式，让自适应重新接管分辨率
                var fill = !(root.TryGetProperty("fill", out var pf) && pf.ValueKind == System.Text.Json.JsonValueKind.False);
                var vwOk = fill && vw >= 320 && vh >= 240;
                // 先记下来：独立屏可能还没建好，建好后由 ClientScreenManager 取走补应用
                state.ViewportW = vwOk ? vw : 0;
                state.ViewportH = vwOk ? vh : 0;
                if (state.OwnPipeline || PerClientScreens)
                {
                    // 这台设备有自己的屏（或马上会有）：只改它自己那条
                    if (vwOk) _logger.Info($"客户端 {conn.ConnectionInfo.ClientIpAddress} 屏幕尺寸: {vw}x{vh}");
                    try { ClientViewportEvent?.Invoke(conn, vwOk ? vw : 0, vwOk ? vh : 0); } catch { }
                    return;
                }
                if (vwOk)
                {
                    _logger.Info($"客户端屏幕尺寸: {vw}x{vh}（按此分辨率输出）");
                    ViewportRequest?.Invoke(vw, vh);
                }
                else if (!fill)
                {
                    ViewportRequest?.Invoke(0, 0);
                }
                return;
            }

            // 扩展屏上「投放 / 清空程序」：客户端随时操作，不需要回电脑面板重新配置
            if (type == "virtualadd" || type == "virtualclear")
            {
                if (type == "virtualclear") VirtualClearRequest?.Invoke();
                else
                {
                    var hwnd = root.TryGetProperty("hwnd", out var vh) && vh.TryGetInt64(out var hv) ? hv : 0;
                    var vtitle = root.TryGetProperty("title", out var vt) ? (vt.GetString() ?? "") : "";
                    if (hwnd != 0)
                    {
                        _logger.Info($"客户端请求把程序投放到扩展屏: hwnd={hwnd}");
                        VirtualAddRequest?.Invoke(new IntPtr(hwnd), vtitle);
                    }
                }
                conn.Send($"{{\"type\":\"virtual\",\"count\":{VirtualWindowCount},"
                          + $"\"source\":{CurrentSource.ToJson()},\"list\":{BuildWindowsJson()}}}");
                return;
            }

            if (type == "ping")
            {
                conn.Send($"{{\"type\":\"pong\",\"t\":{Environment.TickCount64}}}");
                // 客户端还没进入已连接态（说明它没收到 auth_ok）→ 补发一次。
                // 加 5 秒节流：否则一个一直 ping 的客户端会被反复灌 auth_ok。
                if (Environment.TickCount64 - state.LastAuthOkAt > 5000)
                {
                    SendAuthOk(conn, state);
                }
            }
        }
    }

    private void HandleAuth(IWebSocketConnection conn, ClientState state, JsonElement root)
    {
        var ip = conn.ConnectionInfo.ClientIpAddress;
        var pwd = root.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";

        if (IsAuthBanned(ip))
        {
            _logger.Warn($"拒绝鉴权请求（该地址已被临时封禁）: {ip}");
            try { conn.Send("{\"type\":\"auth_fail\"}"); } catch { }
            try { conn.Close(); } catch { }
            return;
        }

        if (SlowEquals(pwd, _cfg.Password))
        {
            state.Authed = true;
            state.FailedAttempts = 0;
            ClearAuthFailure(ip);

            // 客户端能力声明：h264（WebCodecs）/ mse（MediaSource + fMP4）/ jpeg（静态帧兜底）
            var want = root.TryGetProperty("codec", out var c) ? (c.GetString() ?? "").ToLowerInvariant() : "h264";
            state.Jpeg = want == "jpeg";
            state.Mse = want == "mse";
            if (state.Mse) lock (_lock) _mseGeneration++;
            var mode = state.Jpeg ? "jpeg" : state.Mse ? "mse" : "h264";
            _logger.Info($"鉴权通过: {ip}（模式: {mode.ToUpperInvariant()}）");

            SendAuthOk(conn, state);
            try { ClientAuthedEvent?.Invoke(conn); } catch { }

            if (state.Jpeg) return;     // JPEG 客户端等管线推静态帧即可

            if (state.Mse)
            {
                // 已经在跑就把当前 init segment 立刻补给它，避免干等到下一个关键帧
                byte[]? init;
                lock (_videoLock) init = _mseInit;
                if (init != null)
                {
                    try { conn.Send(BuildMseInitMessage(init)); } catch { }
                }
                RequestKeyFrame();
                return;
            }

            byte[]? key;
            lock (_videoLock) key = _lastKeyPacket;
            if (key != null)
            {
                try { conn.Send(key); } catch { }
            }
            else
            {
                RequestKeyFrame();
            }
        }
        else
        {
            state.FailedAttempts++;
            RecordAuthFailure(ip);
            _logger.Warn($"鉴权失败: {ip}（第 {state.FailedAttempts} 次）");
            try { conn.Send("{\"type\":\"auth_fail\"}"); } catch { }
            if (state.FailedAttempts >= 5)
            {
                _logger.Warn($"鉴权失败次数过多，断开: {ip}");
                try { conn.Close(); } catch { }
            }
        }
    }

    /// <summary>
    /// 把 auth_ok 发给客户端。单独提出来是因为要支持“重发”：
    /// 个别手机浏览器会吞掉连接建立后的第一条文本帧，客户端收不到 auth_ok 就会一直卡在密码界面，
    /// 而画面却已经在动。客户端没进入已连接态时会周期性发 ping，这里收到 ping 就再补发一次。
    /// </summary>
    private void SendAuthOk(IWebSocketConnection conn, ClientState state)
    {
        var mode = state.Jpeg ? "jpeg" : state.Mse ? "mse" : "h264";
        state.LastAuthOkAt = Environment.TickCount64;
        string codec, desc;
        int w, h;
        lock (_videoLock)
        {
            codec = _codecString;
            desc = _descriptionBase64;
            w = _videoW;
            h = _videoH;
        }

        var json = $"{{\"type\":\"auth_ok\",\"mode\":\"{mode}\"," +
                   $"\"codec\":\"{codec}\",\"width\":{w},\"height\":{h}," +
                   $"\"fps\":{_cfg.Fps},\"description\":\"{desc}\",\"input\":{(_cfg.AllowRemoteInput ? "true" : "false")}," +
                   $"\"monitor\":{CurrentMonitor},\"monitors\":{BuildMonitorsJson()}," +
                   $"\"virtual\":{VirtualWindowCount},\"source\":{CurrentSource.ToJson()}}}";

        try
        {
            // 双通道下发：文本帧给正常浏览器，二进制帧（首字节 4 = UTF-8 JSON）给
            // 那些会吞掉文本帧的手机浏览器。二进制通道在本机 MIUI 上是稳定的。
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var packet = new byte[bytes.Length + 1];
            packet[0] = 4;
            Buffer.BlockCopy(bytes, 0, packet, 1, bytes.Length);
            conn.Send(packet);
            conn.Send(json);
        }
        catch (Exception ex)
        {
            _logger.Warn($"发送 auth_ok 失败 {conn.ConnectionInfo.ClientIpAddress}: {ex.Message}");
        }
    }

    private void HandleInput(IWebSocketConnection conn, ClientState state, JsonElement root)
    {
        if (!_cfg.AllowRemoteInput) return;

        // 简单的每秒窗口限流，防止脚本刷事件
        var now = Environment.TickCount64;
        if (now - state.InputWindowStart >= 1000)
        {
            state.InputWindowStart = now;
            state.InputCount = 0;
        }
        if (++state.InputCount > _cfg.MaxInputEventsPerSec) return;

        var ev = root.TryGetProperty("ev", out var e) ? e.GetString() : null;
        if (ev == null) return;

        var x = ReadFloat(root, "x", 0f);
        var y = ReadFloat(root, "y", 0f);

        // 有独立扩展屏的设备，输入必须按它自己那块屏换算；没有才用共享画面那个映射器
        var input = _touch;
        try
        {
            var own = InputTargetResolver?.Invoke(conn);
            if (own != null) input = own;
        }
        catch
        {
        }

        try
        {
            switch (ev)
            {
                case "move":
                    input.Move(x, y);
                    _stats.AddInput(1);
                    break;
                case "down":
                    input.Down(x, y, ReadString(root, "button", "left"));
                    _stats.AddInput(1);
                    break;
                case "up":
                    input.Up(x, y, ReadString(root, "button", "left"));
                    _stats.AddInput(1);
                    break;
                case "wheel":
                    input.Wheel(x, y, (int)Math.Clamp(ReadFloat(root, "delta", 0f), -5000f, 5000f));
                    _stats.AddInput(1);
                    break;
                case "pen_down":
                    input.PenDown(x, y, ReadFloat(root, "pressure", 0.5f));
                    break;
                case "pen_move":
                    input.PenMove(x, y, ReadFloat(root, "pressure", 0.5f));
                    break;
                case "pen_up":
                    input.PenUp(x, y);
                    break;
                case "key_down":
                {
                    var vk = (int)ReadFloat(root, "vk", 0f);
                    if (vk <= 0 || vk > 0xFF) break;
                    state.PressedKeys.Add(vk);
                    if (_keyboard.Key(vk, true)) _stats.AddInput(1);
                    break;
                }
                case "key_up":
                {
                    var vk = (int)ReadFloat(root, "vk", 0f);
                    if (vk <= 0 || vk > 0xFF) break;
                    state.PressedKeys.Remove(vk);
                    if (_keyboard.Key(vk, false)) _stats.AddInput(1);
                    break;
                }
                case "text":
                {
                    // KEYEVENTF_UNICODE 直接送字符，不依赖目标机键盘布局
                    var typed = _keyboard.TypeText(ReadString(root, "text", ""));
                    if (typed > 0) _stats.AddInput(typed);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"输入注入失败 {ev}: {ex.Message}");
        }
    }

    private static float ReadFloat(JsonElement root, string name, float fallback)
    {
        if (!root.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => (float)v.GetDouble(),
            JsonValueKind.String => float.TryParse(v.GetString(), out var f) ? f : fallback,
            _ => fallback
        };
    }

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return fallback;
        return v.GetString() ?? fallback;
    }

    private static bool SlowEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        var diff = ba.Length ^ bb.Length;
        var len = Math.Min(ba.Length, bb.Length);
        for (var i = 0; i < len; i++) diff |= ba[i] ^ bb[i];
        return diff == 0 && ba.Length == bb.Length;
    }

    private bool IsAuthBanned(string ip)
    {
        lock (_lock)
        {
            if (!_authFails.TryGetValue(ip, out var f)) return false;
            if (f.BanUntilTicks > Environment.TickCount64) return true;
            f.BanUntilTicks = 0;
            return false;
        }
    }

    /// <summary>失败次数按 1 分钟窗口统计，超过 10 次封禁 1 分钟，防止换连接无限爆破。</summary>
    private void RecordAuthFailure(string ip)
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (!_authFails.TryGetValue(ip, out var f))
            {
                f = new AuthFail { WindowStartTicks = now };
                _authFails[ip] = f;
            }
            if (now - f.WindowStartTicks > 60_000)
            {
                f.Count = 0;
                f.WindowStartTicks = now;
            }
            f.Count++;
            if (f.Count < 10) return;
            f.Count = 0;
            f.BanUntilTicks = now + 60_000;
            _logger.Warn($"同一地址鉴权失败过多，封禁 60 秒: {ip}");
        }
    }

    private void ClearAuthFailure(string ip)
    {
        lock (_lock) _authFails.Remove(ip);
    }

    /// <summary>编码器重建后同步给客户端（分辨率/描述符可能变了）。</summary>
    public void UpdateVideoInfo(VideoEncoder encoder)
    {        string codec = encoder.CodecString();
        var desc = encoder.Extradata;
        var descBase64 = desc != null ? Convert.ToBase64String(desc) : "";
        int w = encoder.Width, h = encoder.Height;

        lock (_videoLock)
        {
            _codecString = codec;
            _descriptionBase64 = descBase64;
            _videoW = w;
            _videoH = h;
            _lastKeyPacket = null;
            _mseInit = null;              // 分辨率/参数变了，旧的 init segment 作废
        }

        // 只同步给还在吃共享画面的客户端：独立屏有自己的编码参数，不能被这块屏的覆盖
        SendTextToShared($"{{\"type\":\"video_info\",\"codec\":\"{codec}\",\"width\":{w},\"height\":{h}," +
                         $"\"fps\":{_cfg.Fps},\"description\":\"{descBase64}\"}}");
        _logger.Info($"编码参数同步: {codec} {w}x{h} desc={descBase64.Length}B");
    }

    /// <summary>只发给一个连接（每客户端独立管线用）。</summary>
    public bool SendBinary(IWebSocketConnection conn, byte[] packet)
    {
        try
        {
            if (!conn.IsAvailable) return false;
            conn.Send(packet);
            _traffic.AddTcp(packet.Length);
            return true;
        }
        catch { return false; }
    }

    /// <summary>只发给一个连接的文本消息。</summary>
    public bool SendText(IWebSocketConnection conn, string json)
    {
        try
        {
            if (!conn.IsAvailable) return false;
            conn.Send(json);
            return true;
        }
        catch { return false; }
    }
    /// <summary>标记该客户端已由自己的管线服务（不再吃全局广播）。</summary>
    public void SetOwnPipeline(IWebSocketConnection conn, bool own)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(conn, out var s)) s.OwnPipeline = own;
        }
    }

    /// <summary>该连接是否走 JPEG 静态帧通道（独立管线据此决定要不要编码 JPEG）。</summary>
    public bool IsJpegClient(IWebSocketConnection conn)
    {
        lock (_lock) return _clients.TryGetValue(conn, out var s) && s.Jpeg;
    }

    /// <summary>
    /// 独立管线发帧前消耗带宽配额：与共享管线共用同一份 ACL 限流，
    /// 保证 ClientMaxBitRate 对"接管了独立扩展屏"的客户端同样生效。
    /// </summary>
    public bool TryConsumeClientQuota(string ip, long bytes) => _acl.TryConsume(ip, bytes);

    /// <summary>该连接是否走 fMP4 / MediaSource 通道（独立管线据此决定要不要封装 MP4）。</summary>
    public bool IsMseClient(IWebSocketConnection conn)
    {
        lock (_lock) return _clients.TryGetValue(conn, out var s) && s.Mse;
    }

    /// <summary>该连接最近一次上报的屏幕尺寸（0 = 没报过或已切回完整模式）。</summary>
    public (int W, int H) GetClientViewport(IWebSocketConnection conn)
    {
        lock (_lock)
        {
            if (!_clients.TryGetValue(conn, out var s)) return (0, 0);
            return (s.ViewportW, s.ViewportH);
        }
    }

    /// <summary>只给一个连接下发 fMP4 的 init segment（独立管线用）。</summary>
    public void SendMseInit(IWebSocketConnection conn, byte[] init)
    {
        try { SendText(conn, BuildMseInitMessage(init)); } catch { }
    }

    public void RequestKeyFrame() => _keyFrameRequested = true;

    public bool ConsumeKeyFrameRequest()
    {
        if (!_keyFrameRequested) return false;
        _keyFrameRequested = false;
        return true;
    }

    /// <summary>把一帧编码数据发给所有已鉴权客户端；首字节标记是否关键帧。</summary>
    public void BroadcastFrame(byte[] data, bool keyFrame)
    {
        var packet = new byte[data.Length + 1];
        packet[0] = keyFrame ? (byte)1 : (byte)0;
        Buffer.BlockCopy(data, 0, packet, 1, data.Length);

        if (keyFrame)
        {
            lock (_videoLock) _lastKeyPacket = packet;
        }

        List<(IWebSocketConnection conn, string ip)> targets = new();
        lock (_lock)
        {
            foreach (var kv in _clients)
            {
                if (!kv.Value.Authed || !kv.Key.IsAvailable) continue;
                if (kv.Value.OwnPipeline) continue;   // 有自己管线的客户端不吃全局流
                if (kv.Value.Jpeg) continue;      // JPEG 客户端走静态帧通道，不喂 H.264
                if (kv.Value.Mse) continue;       // MSE 客户端走 fMP4 通道，不喂裸 H.264
                targets.Add((kv.Key, kv.Key.ConnectionInfo.ClientIpAddress));
            }
        }

        foreach (var (conn, ip) in targets)
        {
            if (!_acl.TryConsume(ip, packet.Length))
            {
                continue;   // 超出该客户端的带宽配额，本帧跳过
            }
            try
            {
                conn.Send(packet);
                _traffic.AddTcp(packet.Length);
            }
            catch (Exception ex)
            {
                _logger.Debug($"发送失败 {ip}: {ex.Message}");
            }
        }
    }

    /// <summary>当前有多少客户端走 JPEG 静态帧通道（0 时管线不必做 JPEG 编码）。</summary>
    public int JpegClients
    {
        get
        {
            lock (_lock)
            {
                var n = 0;
                foreach (var kv in _clients)
                {
                    if (kv.Value.Authed && kv.Value.Jpeg && !kv.Value.OwnPipeline && kv.Key.IsAvailable) n++;
                }
                return n;
            }
        }
    }

    /// <summary>当前有多少客户端走 fMP4 / MediaSource 通道（0 时管线不必封装 MP4）。</summary>
    public int MseClients
    {
        get
        {
            lock (_lock)
            {
                var n = 0;
                foreach (var kv in _clients)
                {
                    if (kv.Value.Authed && kv.Value.Mse && !kv.Value.OwnPipeline && kv.Key.IsAvailable) n++;
                }
                return n;
            }
        }
    }

    /// <summary>MSE 客户端代次：每有一个新的 MSE 客户端接入就 +1，管线据此重开封装器。</summary>
    public int MseGeneration
    {
        get { lock (_lock) return _mseGeneration; }
    }

    private static string BuildMseInitMessage(byte[] init) =>
        $"{{\"type\":\"fmp4_init\",\"data\":\"{Convert.ToBase64String(init)}\"}}";

    /// <summary>管线建好 muxer 后登记 init segment，并补发给还在共享画面上的 MSE 客户端。</summary>
    public void SetMseInit(byte[] init)
    {
        lock (_videoLock) _mseInit = init;
        List<IWebSocketConnection> targets = new();
        lock (_lock)
        {
            foreach (var kv in _clients)
            {
                if (!kv.Value.Authed || !kv.Value.Mse || kv.Value.OwnPipeline || !kv.Key.IsAvailable) continue;
                targets.Add(kv.Key);
            }
        }
        var json = BuildMseInitMessage(init);
        foreach (var conn in targets)
        {
            try { conn.Send(json); } catch { }
        }
    }

    /// <summary>推一段 fMP4 分片：首字节 3 作为类型标记。</summary>
    public void BroadcastFmp4(byte[] fragment)
    {
        var packet = new byte[fragment.Length + 1];
        packet[0] = 3;
        Buffer.BlockCopy(fragment, 0, packet, 1, fragment.Length);

        List<(IWebSocketConnection conn, string ip)> targets = new();
        lock (_lock)
        {
            foreach (var kv in _clients)
            {
                if (!kv.Value.Authed || !kv.Value.Mse || !kv.Key.IsAvailable) continue;
                if (kv.Value.OwnPipeline) continue;   // 有自己管线的客户端只吃自己那块屏
                targets.Add((kv.Key, kv.Key.ConnectionInfo.ClientIpAddress));
            }
        }

        foreach (var (conn, ip) in targets)
        {
            if (!_acl.TryConsume(ip, packet.Length)) continue;
            try
            {
                conn.Send(packet);
                _traffic.AddTcp(packet.Length);
            }
            catch (Exception ex)
            {
                _logger.Debug($"fMP4 发送失败 {ip}: {ex.Message}");
            }
        }
    }

    /// <summary>推一帧 JPEG：首字节 2 作为类型标记，其余是完整 JPEG 数据。</summary>
    public void BroadcastJpeg(byte[] jpeg)
    {
        var packet = new byte[jpeg.Length + 1];
        packet[0] = 2;
        Buffer.BlockCopy(jpeg, 0, packet, 1, jpeg.Length);

        List<(IWebSocketConnection conn, string ip)> targets = new();
        lock (_lock)
        {
            foreach (var kv in _clients)
            {
                if (!kv.Value.Authed || !kv.Value.Jpeg || !kv.Key.IsAvailable) continue;
                if (kv.Value.OwnPipeline) continue;   // 有自己管线的客户端只吃自己那块屏
                targets.Add((kv.Key, kv.Key.ConnectionInfo.ClientIpAddress));
            }
        }

        foreach (var (conn, ip) in targets)
        {
            // JPEG 单帧 60~90KB，20fps 就超过 1MB/s；沿用主链路 6Mbps 的预算会整帧被丢弃
            var jpegLimit = Math.Max(_cfg.ClientMaxBitRate / 8, (long)_cfg.JpegFps * 110 * 1024);
            if (!_acl.TryConsume(ip, packet.Length, jpegLimit)) continue;
            try
            {
                conn.Send(packet);
                _traffic.AddTcp(packet.Length);
            }
            catch (Exception ex)
            {
                _logger.Debug($"JPEG 发送失败 {ip}: {ex.Message}");
            }
        }
    }

    private void BroadcastText(string json)
    {
        List<IWebSocketConnection> targets = new();
        lock (_lock)
        {
            foreach (var kv in _clients)
            {
                if (kv.Value.Authed && kv.Key.IsAvailable) targets.Add(kv.Key);
            }
        }
        foreach (var conn in targets)
        {
            try { conn.Send(json); } catch { }
        }
    }

    /// <summary>只发给还在共享画面上的已鉴权客户端（独立屏有自己的一套参数）。</summary>
    private void SendTextToShared(string json)
    {
        List<IWebSocketConnection> targets = new();
        lock (_lock)
        {
            foreach (var kv in _clients)
            {
                if (kv.Value.Authed && !kv.Value.OwnPipeline && kv.Key.IsAvailable) targets.Add(kv.Key);
            }
        }
        foreach (var conn in targets)
        {
            try { conn.Send(json); } catch { }
        }
    }

    /// <summary>每 2 秒推一次统计，并清理超时未鉴权的连接。</summary>
    private void WatchdogLoop()
    {
        while (_running)
        {
            Thread.Sleep(1000);
            try
            {
                long timeout = _cfg.AuthTimeoutMs;
                List<IWebSocketConnection> expired = new();
                lock (_lock)
                {
                    foreach (var kv in _clients)
                    {
                        if (!kv.Value.Authed && Environment.TickCount64 - kv.Value.ConnectedAt > timeout)
                        {
                            expired.Add(kv.Key);
                        }
                    }
                }
                foreach (var conn in expired)
                {
                    _logger.Warn($"未鉴权超时，断开 {conn.ConnectionInfo.ClientIpAddress}");
                    try { conn.Close(); } catch { }
                    lock (_lock) _clients.Remove(conn);
                }

                // 全局统计只发给共享画面的客户端：独立屏有自己的管线，混着发会导致
                // 抓屏状态在两个值之间来回跳（手机端画面跟着闪）
                SendTextToShared(BuildStatsMessage());

                // 锁屏同步：锁屏后截不到画面，主动告诉客户端"已锁屏"，别让手机停在最后一帧
                var locked = LockState.IsScreenUnavailable();
                if (locked != _lastLocked)
                {
                    _lastLocked = locked;
                    _logger.Info(locked ? "检测到本机锁屏，通知客户端" : "本机已解锁，恢复画面");
                    BroadcastText($"{{\"type\":\"lock\",\"locked\":{(locked ? "true" : "false")}}}");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"watchdog: {ex.Message}");
            }
        }
    }

    private string BuildStatsMessage() =>
        $"{{\"type\":\"stats\",\"fps\":{_stats.Fps},\"kbps\":{_stats.Kbps},\"clients\":{AuthedClients}," +
        $"\"capturing\":{(_stats.Capturing ? "true" : "false")}}}";

    /// <summary>客户端请求切换采集显示器时回调（由 StreamPipeline 实现）。</summary>
    public Action<int>? MonitorRequest { get; set; }

    /// <summary>客户端请求切换采集源（显示器 / 窗口）时回调。</summary>
    public Action<CaptureSource>? SourceRequest { get; set; }

    private volatile int _currentMonitor;
    private CaptureSource _currentSource = CaptureSource.Monitor(0);

    public int CurrentMonitor
    {
        get => _currentMonitor;
        set => _currentMonitor = value;
    }

    public CaptureSource CurrentSource
    {
        get => _currentSource;
        set => _currentSource = value;
    }

    /// <summary>扩展屏里已配置的程序数量（客户端据此显示「N 个程序 / 空屏」）。</summary>
    public int VirtualWindowCount { get; set; }

    /// <summary>
    /// 是否每台设备都会拿到自己的扩展屏（Parsec VDD 可用）。
    /// 为 true 时客户端上报的分辨率一律交给它自己那块屏，不能改共享画面的输出尺寸。
    /// </summary>
    public bool PerClientScreens { get; set; }

    /// <summary>手机端请求把某个程序投放到扩展屏 / 清空扩展屏。</summary>
    public Action<IntPtr, string>? VirtualAddRequest { get; set; }
    public Action? VirtualClearRequest { get; set; }

    /// <summary>客户端上报自身屏幕尺寸（扩展屏按这个尺寸出画）。</summary>
    public event Action<IWebSocketConnection>? ClientAuthedEvent;    // 鉴权通过：给这台设备建自己的屏/管线
    public event Action<IWebSocketConnection>? ClientClosedEvent;    // 断开：回收它自己的屏/管线
    public event Action<IWebSocketConnection, int, int>? ClientViewportEvent;  // 它自己那块屏的分辨率

    public Action<int, int>? ViewportRequest { get; set; }

    /// <summary>
    /// 这条连接该用哪个输入映射器。返回 null 表示"没有自己的扩展屏"，退回共享画面那个。
    /// 每台设备的独立屏位置各不相同，输入必须按它自己那块屏的矩形换算，
    /// 否则手机上点的位置会落到主屏上（表现为"客户端变成了一块触摸板"）。
    /// </summary>
    public Func<IWebSocketConnection, TouchForwarder?>? InputTargetResolver { get; set; }

    /// <summary>可采集的窗口清单（HTTP /windows 与 WS 的 windows 请求共用）。</summary>
    public string BuildWindowsJson()
    {
        var wins = WindowSource.List((uint)Environment.ProcessId);
        var sb = new StringBuilder("{\"windows\":[");
        for (var i = 0; i < wins.Count; i++)
        {
            var w = wins[i];
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"hwnd\":{w.Handle},\"title\":{JsonText.Quote(w.Title)}," +
                      $"\"process\":{JsonText.Quote(w.Process)},\"w\":{w.Width},\"h\":{w.Height}," +
                      $"\"min\":{(w.Minimized ? "true" : "false")}}}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>下发给客户端的显示器清单，-1 表示全部显示器拼成的虚拟桌面。</summary>
    private string BuildMonitorsJson()
    {
        var monitors = DisplayCapture.GetMonitors();
        var sb = new StringBuilder("[");
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"index\":{i},\"x\":{m.X},\"y\":{m.Y},\"w\":{m.Width},\"h\":{m.Height}}}");
        }
        if (monitors.Count > 1)
        {
            var v = DisplayCapture.GetVirtualScreen();
            if (monitors.Count > 0) sb.Append(',');   // 少了这一个逗号整个 /status 就不是合法 JSON 了
            sb.Append($"{{\"index\":-1,\"x\":{v.X},\"y\":{v.Y},\"w\":{v.Width},\"h\":{v.Height}}}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private string BuildStatusJson()
    {
        string codec;
        int w, h;
        lock (_videoLock)
        {
            codec = _codecString;
            w = _videoW;
            h = _videoH;
        }

        return $"{{\"uptime\":{_stats.UptimeSeconds},\"fps\":{_stats.Fps},\"kbps\":{_stats.Kbps}," +
               $"\"avgKbps\":{(int)_stats.AvgKbps},\"frames\":{_stats.FramesTotal}," +
               $"\"clients\":{AuthedClients},\"codec\":\"{codec}\",\"width\":{w},\"height\":{h}," +
               $"\"monitor\":{CurrentMonitor},\"monitors\":{BuildMonitorsJson()}," +
               $"\"source\":{CurrentSource.ToJson()}," +
               $"\"inputEvents\":{_stats.InputEvents},\"keysTyped\":{_stats.KeysTyped}," +
               $"\"capturing\":{(_stats.Capturing ? "true" : "false")}," +
               $"\"adaptive\":{(_cfg.EnableAdaptive ? "true" : "false")}," +
               $"\"input\":{(_cfg.AllowRemoteInput ? "true" : "false")}}}";
    }

    /// <summary>允许本机通过 /shutdown?token=... 优雅停机（token 用访问密码）。</summary>
    public void EnableLocalShutdown(Action onShutdown)
    {
        _http.ShutdownToken = _cfg.Password;
        _http.ShutdownRequested = onShutdown;
    }

    public void Dispose()
    {
        _running = false;
        try { _ws?.Dispose(); } catch { }
        _http.Dispose();
        lock (_lock) _clients.Clear();
    }
}




