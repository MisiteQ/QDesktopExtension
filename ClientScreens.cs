using System.Collections.Concurrent;
using System.Windows.Forms;
using Fleck;

namespace QDesktopExtension;

/// <summary>
 /// 一台设备一块屏：客户端鉴权通过 → 用 Parsec VDD 插一块新显示器 → 摆到现有桌面最右侧
 /// → 建一条只服务这个客户端的管线（采集这块屏、按客户端分辨率编码）。
 /// 断开 → 停管线 → 拔屏。已有显示器（笔记本内屏、外接显示器）一律不动。
 /// </summary>
public sealed class ClientScreenManager : IDisposable
{
    private readonly ConfigModel _cfg;
    private readonly Logger _logger;
    private readonly StreamServer _server;
    private readonly InkPen _inkPen;
    private readonly CancellationToken _token;
    private readonly ParsecVdd _vdd = new();
    private readonly ConcurrentDictionary<IWebSocketConnection, Session> _sessions = new();
    // 连接已断开、但还在宽限期内等着同一台设备重连的会话（它们占着的屏先不拔）
    private readonly List<Session> _orphans = new();
    private System.Threading.Timer? _orphanTimer;
    // 最近一次插屏失败的时刻（用于全局冷场，避免连接风暴里反复折腾显示栈）
    private long _lastAddFailTicks;

    /// <summary>插屏 / 拔屏之后给系统显示栈留出的稳定时间（毫秒）。</summary>
    private const int ScreenSettleMs = 400;

    /// <summary>
    /// 客户端断开后保留它那块屏的时长（毫秒）。手机 USB / 网络闪断后往往 1 秒内就重连，
    /// 立刻拔屏再插屏会让系统显示栈高频重建——这正是进程崩溃的触发场景。
    /// 实测 Parsec VDD 0.45 在本机上执行过 REMOVE 之后，同进程内 AddDisplay 会"假成功"
    /// （返回槽位但系统再也不枚举显示器），所以这里不轻易拔屏。
    /// 孤儿屏闲置超过这个时长（10 分钟）才真正拔掉；期间一直留着给设备重连用。
    /// </summary>
    private const int OrphanEvictMs = 10 * 60_000;

    /// <summary>插屏失败后的全局冷场时间：这段时间内不再折腾显示栈，避免把驱动越弄越坏。</summary>
    private const int AddFailCooldownMs = 10_000;

    private sealed class Session
    {
        public int DisplayIndex;
        public string DeviceName = "";
        public string Ip = "";
        public DisplayCapture? Capture;
        public TouchForwarder? Touch;
        public VideoEncoder? Encoder;
        public StreamPipeline? Pipeline;
        public ClientPipelineHost? Host;
        public CancellationTokenSource? Stop;
        public Thread? Runner;
        public int PendingW;   // 建屏途中收到的客户端屏幕尺寸，管线装好后补应用
        public int PendingH;
        public long OrphanSince;   // 连接断开、等待重连的时刻（0 = 仍在使用）
        public volatile bool Closed;
    }

    public ClientScreenManager(ConfigModel cfg, Logger logger, StreamServer server,
                               InkPen inkPen, CancellationToken token)
    {
        _cfg = cfg;
        _logger = logger;
        _server = server;
        _inkPen = inkPen;
        _token = token;

        if (_vdd.Open())
        {
            _server.PerClientScreens = true;
            // 先订事件再清理：清理可能要好几秒，这期间接入的客户端不能漏掉
            _server.ClientAuthedEvent += OnAuthed;
            _server.ClientClosedEvent += OnClosed;
            _server.ClientViewportEvent += OnViewport;

            // 上次进程崩溃/被强杀时来不及拔掉的虚拟屏，启动时先清掉：
            // 必须在开始保活之前做，否则新进程的 UPDATE 会给旧屏"续命"，
            // 于是每重启一次就多留一块屏，一路累积下去。和 Attach 互斥，
            // 免得清理正拔屏时又插进一块来。
            // 优先按上次记录的槽位精确清除：盲扫全部槽位会让驱动进入"拆除中"状态，
            // 之后十几秒内 AddDisplay 全都返回 -1（日志里启动后 15 秒插不出屏就是这个原因）。
            int attached;
            lock (this)
            {
                attached = VirtualDisplayMode.CountAttachedParsecScreens();
                var known = LoadSlots();
                foreach (var idx in known) _vdd.RemoveDisplay(idx);
                if (known.Count > 0) Thread.Sleep(200);

                // 兜底：系统里还看得到残留虚拟屏（说明上次记录的槽位不全）才盲扫
                if (VirtualDisplayMode.CountAttachedParsecScreens() > 0)
                {
                    var removed = _vdd.RemoveStaleDisplays();
                    if (removed > 0) _logger.Info($"盲扫额外清理了 {removed} 块残留虚拟屏");
                }
                SaveSlots();
            }
            if (attached > 0) _logger.Info($"启动时检测到残留虚拟屏 {attached} 块，已清理");
            _logger.Info($"启动时显示器 {Screen.AllScreens.Length} 块");

            _vdd.StartKeepAlive();
            // 断线宽限期的清理：到点还没重连的会话才真正拔屏
            _orphanTimer = new System.Threading.Timer(_ => SweepOrphans(), null, 1000, 1000);
            _logger.Info($"Parsec VDD 已就绪（驱动版本 {_vdd.Version}）：每台设备将获得自己的一块扩展屏");
        }
        else
        {
            _logger.Warn("Parsec VDD 打不开，本次运行退化为“所有设备共享一块屏”");
        }
    }

    /// <summary>驱动是否可用（面板展示用）。</summary>
    public bool DriverReady => _vdd.IsOpen;

    /// <summary>当前有几块"属于客户端"的屏。</summary>
    public int ScreenCount => _sessions.Count + _orphans.Count;

    /// <summary>一块独立扩展屏的快照（面板展示用）。</summary>
    public sealed record ClientScreenInfo(string Device, string Ip, bool Orphan, string Size);

    /// <summary>列出当前所有独立扩展屏（在用的 + 断线等待重连的），面板刷新时调用。</summary>
    public List<ClientScreenInfo> SnapshotScreens()
    {
        List<(string Device, string Ip, bool Orphan)> raw;
        lock (this)
        {
            raw = _sessions.Values.Select(s => (s.DeviceName, s.Ip, false))
                .Concat(_orphans.Select(s => (s.DeviceName, s.Ip, true)))
                .ToList();
        }
        // 屏幕尺寸查询放锁外：Attach 建屏期间也持着这把锁，锁内再查 Screen 会互相拖慢
        return raw.Select(t =>
        {
            var scr = Screen.AllScreens.FirstOrDefault(x => x.DeviceName == t.DeviceName);
            return new ClientScreenInfo(t.DeviceName, t.Ip, t.Orphan,
                scr != null ? $"{scr.Bounds.Width}x{scr.Bounds.Height}" : "未枚举");
        }).ToList();
    }

    /// <summary>
    /// 这条连接的输入该按哪块屏的坐标换算：有自己的扩展屏就用它自己那块，
    /// 还没建好屏（或驱动不可用）时返回 null，由服务端退回共享画面的映射器。
    /// </summary>
    public TouchForwarder? ResolveTouch(IWebSocketConnection conn)
        => _sessions.TryGetValue(conn, out var s) ? s.Touch : null;

    private void OnAuthed(IWebSocketConnection conn)
    {
        // 建屏要 1~3 秒：鉴权一通过就接管这条连接，别让共享画面（物理屏）再推给它
        _server.SetOwnPipeline(conn, true);
        // 建屏要轮询设备枚举，不能占着 WebSocket 线程
        Task.Run(() => BuildScreenWithRetry(conn));
    }

    /// <summary>
    /// 建屏失败（驱动暂时插不出屏、虚拟屏已达上限等）时不阻塞这条连接：
    /// 先让它回到共享画面，之后每 2 秒重试一次，成功后再切到自己的扩展屏。
    /// 这样显示栈抖动只会让"扩展屏"晚一点出现，而不是让客户端一直黑屏或反复重连。
    /// </summary>
    private void BuildScreenWithRetry(IWebSocketConnection conn)
    {
        var ip = conn.ConnectionInfo.ClientIpAddress;
        for (var attempt = 0; ; attempt++)
        {
            if (_token.IsCancellationRequested || !conn.IsAvailable) return;

            var ok = false;
            try { ok = Attach(conn); }
            catch (Exception ex) { _logger.Warn($"建屏异常（第 {attempt + 1} 次）：{ex.Message}"); }

            if (ok || !conn.IsAvailable || _token.IsCancellationRequested) return;

            // 退回共享画面：至少先有画面，别让客户端空等
            try { _server.SetOwnPipeline(conn, false); } catch { }

            if (attempt >= 6)
            {
                _logger.Warn($"{ip} 独立扩展屏创建失败，本次连接保持共享画面");
                return;
            }
            _logger.Info($"{ip} 独立扩展屏暂不可用，{AddFailCooldownMs / 1000} 秒后重试（第 {attempt + 1} 次）");
            Thread.Sleep(AddFailCooldownMs);
        }
    }

    private void OnClosed(IWebSocketConnection conn)
    {
        try { _server.SetOwnPipeline(conn, false); } catch { }
        lock (this)
        {
            if (!_sessions.TryRemove(conn, out var s)) return;
            // 先不拔屏：手机 USB 闪断后通常一秒内就重连，沿用同一块屏比"拔了再插"稳得多
            s.OrphanSince = Environment.TickCount64;
            _orphans.Add(s);
            SaveSlots();
            _logger.Info($"客户端断开：显示器 {s.DeviceName} 保留 {OrphanEvictMs / 60000} 分钟等待重连");
        }
    }

    /// <summary>把宽限期内的孤儿屏交给新连接（前提：同一 IP，且管线已经建好）。</summary>
    private bool TryInherit(IWebSocketConnection conn, string ip)
    {
        // 1) 连接已断开、还在宽限期内的会话
        for (var i = 0; i < _orphans.Count; i++)
        {
            var s = _orphans[i];
            if (!string.Equals(s.Ip, ip, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Closed || s.Pipeline == null || s.Host == null) continue;
            _orphans.RemoveAt(i);
            Adopt(conn, s);
            return true;
        }

        // 2) 旧连接还"活着"但已是僵尸（关闭事件还没到）的会话：同样接管，避免多插一块屏
        foreach (var kv in _sessions.ToArray())
        {
            var s = kv.Value;
            if (!string.Equals(s.Ip, ip, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Closed || s.Pipeline == null || s.Host == null || kv.Key.IsAvailable) continue;
            if (!_sessions.TryRemove(kv.Key, out _)) continue;
            try { _server.SetOwnPipeline(kv.Key, false); } catch { }
            Adopt(conn, s);
            return true;
        }
        return false;
    }

    /// <summary>把已有会话（连同那块屏）转交给新连接，不重建采集 / 编码 / 显示器。</summary>
    private void Adopt(IWebSocketConnection conn, Session s)
    {
        s.OrphanSince = 0;
        s.Host!.Retarget(conn);
        _sessions[conn] = s;
        _server.SetOwnPipeline(conn, true);
        try { if (s.Encoder != null) s.Host.UpdateVideoInfo(s.Encoder); } catch { }
        s.Host.RequestKeyFrame();
        var (vpW, vpH) = _server.GetClientViewport(conn);
        if (vpW > 0 && vpH > 0)
        {
            try { s.Pipeline!.OnClientViewport(vpW, vpH); } catch { }
        }
        _logger.Info($"同一设备 {s.Ip} 重连：沿用已有显示器 {s.DeviceName}，不再重新插屏");
    }

    /// <summary>孤儿屏闲置太久（10 分钟）才真正拔掉；期间一直留着等设备重连。</summary>
    private void SweepOrphans()
    {
        lock (this)
        {
            var now = Environment.TickCount64;
            for (var i = _orphans.Count - 1; i >= 0; i--)
            {
                var s = _orphans[i];
                if (s.OrphanSince != 0 && now - s.OrphanSince < OrphanEvictMs) continue;
                _orphans.RemoveAt(i);
                CloseSession(s);
                _logger.Info($"显示器 {s.DeviceName} 已闲置 {OrphanEvictMs / 60000} 分钟，拔掉回收");
            }
            SaveSlots();
        }
    }

    /// <summary>
    /// 槽位不够时回收闲置最久的那块孤儿屏，给新设备腾地方（不主动拔正常在用的屏）。
    /// </summary>
    private bool EvictOldestOrphan()
    {
        Session? oldest = null;
        foreach (var s in _orphans)
        {
            if (oldest == null || s.OrphanSince < oldest.OrphanSince) oldest = s;
        }
        if (oldest == null) return false;
        _orphans.Remove(oldest);
        CloseSession(oldest);
        SaveSlots();
        _logger.Info($"槽位已满，回收闲置的显示器 {oldest.DeviceName} 给新设备");
        return true;
    }

    /// <summary>清掉连接已断、又没有屏可接管的僵尸会话。</summary>
    private void CleanupDeadSessions()
    {
        foreach (var kv in _sessions.ToArray())
        {
            if (kv.Key.IsAvailable) continue;
            if (_sessions.TryRemove(kv.Key, out var s))
            {
                CloseSession(s);
                _logger.Info($"回收僵尸会话的显示器 {s.DeviceName}");
            }
        }
        SaveSlots();
    }

    /// <summary>先停编码线程，再释放它正在访问的采集缓冲与 FFmpeg 原生对象。</summary>
    private void CloseSession(Session s)
    {
        if (s.Closed) return;
        s.Closed = true;
        try { s.Stop?.Cancel(); } catch { }
        if (s.Runner != null && s.Runner != Thread.CurrentThread)
        {
            // 有上限地等：管线线程卡在 BitBlt 里时可能迟迟不退，无限 Join 会把整个管理器锁死
            try
            {
                if (!s.Runner.Join(TimeSpan.FromSeconds(8)))
                    _logger.Warn($"独立扩展屏管线线程 8 秒内未退出（{s.DeviceName}），继续回收其余资源");
            }
            catch
            {
            }
        }
        try { s.Pipeline?.Dispose(); } catch { }
        try { s.Capture?.Dispose(); } catch { }
        try { s.Encoder?.Dispose(); } catch { }
        try { s.Stop?.Dispose(); } catch { }
        try
        {
            // 拔屏和 BitBlt 必须互斥：桌面表面正在重建时抓屏会让 GDI 直接崩进程
            lock (DisplayCapture.ScreenGate) _vdd.RemoveDisplay(s.DisplayIndex);
        }
        catch
        {
        }
        try
        {
            // 拆除是异步的：给系统一点时间把这块屏从桌面上摘掉再走下一个，避免连拔时撞车
            lock (DisplayCapture.ScreenGate) Thread.Sleep(ScreenSettleMs);
        }
        catch
        {
        }
    }

    private void OnViewport(IWebSocketConnection conn, int w, int h)
    {
        if (!_sessions.TryGetValue(conn, out var s)) return;   // 会话还没建：服务端已按连接记下尺寸
        if (s.Pipeline != null)
        {
            // 只改这条管线的输出分辨率：这块屏是它自己的，不影响别人
            s.Pipeline.OnClientViewport(w, h);
        }
        else
        {
            // 正在建屏（AddDisplay + 枚举 + 建编码器），先存到会话里，Attach 装好管线后补上
            s.PendingW = w;
            s.PendingH = h;
        }
    }

    /// <summary>
    /// 给一条客户端连接建一块独立屏。返回 true = 已经拿到自己的屏（新建或沿用），
    /// false = 这次没建成（失败/超限），调用方会把这条连接退回共享画面并稍后重试。
    /// </summary>
    private bool Attach(IWebSocketConnection conn)
    {
        if (!_vdd.IsOpen) return false;

        var ip = conn.ConnectionInfo.ClientIpAddress;

        lock (this)
        {
            if (_sessions.ContainsKey(conn)) return true;
            if (!conn.IsAvailable) return false;
            // 同一台设备（同一个 IP）重连时沿用已有的屏：反复拔插虚拟显示器是崩溃的主要触发点
            if (TryInherit(conn, ip)) return true;
            CleanupDeadSessions();

            // 刚插屏失败过：一段时间内别再折腾显示栈。实测反复"插了又拔"会把驱动搞成
            // "AddDisplay 成功但系统永远不枚举"的状态，之后只能重启服务才恢复。
            if (Environment.TickCount64 - _lastAddFailTicks < AddFailCooldownMs) return false;

            // 上限保护：虚拟屏上限统一取 ParsecVdd.MaxClientScreens；
            // 满了先回收闲置最久的孤儿屏
            if (ScreenCount >= ParsecVdd.MaxClientScreens)
            {
                if (!EvictOldestOrphan())
                {
                    _logger.Warn($"虚拟屏数量已达上限（当前 {ScreenCount}），{ip} 退回共享画面");
                    return false;
                }
            }

            var before = new HashSet<string>(Screen.AllScreens.Select(x => x.DeviceName));
            int index;
            // 插屏期间桌面表面正在重建，不能和抓屏线程撞车。
            // 这里只试一次：失败直接交回调用方（它会让客户端立刻看共享画面，2 秒后再来），
            // 绝不在这里长时间阻塞 —— 显示栈卡住时死等会连带把整条服务拖住。
            lock (DisplayCapture.ScreenGate) index = _vdd.AddDisplay();
            if (index < 0)
            {
                _lastAddFailTicks = Environment.TickCount64;
                _logger.Warn($"给客户端插屏失败（AddDisplay 返回 {index}），先退回共享画面，{AddFailCooldownMs / 1000} 秒后再试");
                return false;
            }
            _logger.Info($"[建屏] 已插屏，槽位 {index}，等待系统枚举显示设备");

            // 等系统把新显示器枚举出来（实测重启显示栈后要 5~8 秒，500ms 太短会误判失败）；
            // 这期间客户端可能已经断开，必须提前退出，
            // 否则会继续用一条死连接建采集/编码，白白折腾几秒（也是崩溃的高发时段）
            Screen? added = null;
            for (var i = 0; i < 30 && added == null; i++)
            {
                if (_token.IsCancellationRequested || !conn.IsAvailable) break;
                added = Screen.AllScreens.FirstOrDefault(x => !before.Contains(x.DeviceName));
                if (added == null) Thread.Sleep(100);
            }
            if (added == null)
            {
                _lastAddFailTicks = Environment.TickCount64;
                _logger.Warn(conn.IsAvailable
                    ? $"插屏后系统没有枚举出新显示器，撤回（驱动可能已经卡住，{AddFailCooldownMs / 1000} 秒内不再尝试）"
                    : "建屏途中客户端已断开，撤回这块新显示器");
                lock (DisplayCapture.ScreenGate) _vdd.RemoveDisplay(index);
                lock (DisplayCapture.ScreenGate) Thread.Sleep(ScreenSettleMs);
                return false;
            }

            // 只摆这一块：放到现有桌面最右侧，别人的分辨率/位置/主屏标记都不动
            var msg = "";
            lock (DisplayCapture.ScreenGate) ScreenArranger.PlaceRightOfDesktop(added.DeviceName, out msg);
            _logger.Info($"新显示器 {added.DeviceName} {added.Bounds.Width}x{added.Bounds.Height} {msg}");

            var session = new Session
            {
                DisplayIndex = index,
                DeviceName = added.DeviceName,
                Ip = ip,
            };
            _sessions[conn] = session;
            SaveSlots();
            // 之前失败重试时可能已退回共享画面，这里重新接管
            _server.SetOwnPipeline(conn, true);

            // 登记会话之后再确认一次连接：OnClosed 只会回收"已经登记"的会话，
            // 这里若已断开，必须自己把刚插的屏收掉，否则会留下一块没人认领的屏
            if (!conn.IsAvailable || _token.IsCancellationRequested)
            {
                _sessions.TryRemove(conn, out _);
                CloseSession(session);
                return false;
            }

            try
            {
                var scr = Screen.AllScreens.FirstOrDefault(x => x.DeviceName == added.DeviceName) ?? added;
                var rect = new ScreenRect(scr.Bounds.X, scr.Bounds.Y, scr.Bounds.Width, scr.Bounds.Height);

                var source = CaptureSource.Monitor(Array.IndexOf(Screen.AllScreens, scr));
                // 直接用刚取到的这块屏的矩形建采集器：按序号再枚举一次可能因为
                // Screen.AllScreens 与 EnumDisplayMonitors 顺序不同而抓到别的屏
                var capture = new DisplayCapture(rect);
                session.Capture = capture;
                _logger.Info($"[建屏] 采集缓冲已就绪 {capture.Width}x{capture.Height}");

                var encoder = new VideoEncoder(_cfg);
                session.Encoder = encoder;
                encoder.Init(rect.Width, rect.Height, capture.Width, capture.Height,
                             _cfg.Fps, _cfg.BitRate);
                _logger.Info("[建屏] 编码器已就绪");
                var adaptive = new AdaptiveController(_cfg, _logger, capture.Width, capture.Height, _cfg.BitRate);

                // 输入映射必须用这块屏自己的区域，否则点到的坐标是错的
                var sessionTouch = new TouchForwarder(_cfg, _inkPen, rect);
                session.Touch = sessionTouch;

                var host = new ClientPipelineHost(_server, conn);
                // 鉴权时下发的是共享画面的尺寸，这块屏是另一个尺寸，得先告诉客户端
                host.UpdateVideoInfo(encoder);
                // 每块独立屏一套统计：共用全局那份会让"抓屏是否可用"被别的管线覆盖，画面来回闪
                var pipeline = new StreamPipeline(_cfg, _logger, capture, encoder, adaptive,
                                                  host, new StreamStats(), sessionTouch,
                                                  source, null);
                session.Pipeline = pipeline;
                session.Host = host;     // 重连时要把这条管线转交给新连接

                // 手机连上来就报过屏幕尺寸，那时还没这块屏：现在补应用，保证 1:1 输出
                var cached = _server.GetClientViewport(conn);
                var vpW = session.PendingW > 0 ? session.PendingW : cached.W;
                var vpH = session.PendingH > 0 ? session.PendingH : cached.H;
                if (vpW > 0 && vpH > 0)
                {
                    pipeline.OnClientViewport(vpW, vpH);
                    _logger.Info($"客户端屏幕尺寸 {vpW}x{vpH} 已应用到新扩展屏");
                }
                else
                {
                    _logger.Info("客户端当前为完整显示模式，独立屏保持虚拟屏原生分辨率");
                }

                host.RequestKeyFrame();

                session.Stop = CancellationTokenSource.CreateLinkedTokenSource(_token);
                var stop = session.Stop;
                var thread = new Thread(() =>
                {
                    try { pipeline.Run(stop.Token); }
                    catch (Exception ex)
                    {
                        if (!stop.IsCancellationRequested)
                            _logger.Error($"独立扩展屏管线异常: {ex.Message}");
                    }
                })
                {
                    IsBackground = true,
                    Name = $"pipeline-{scr.DeviceName}",
                };
                session.Runner = thread;
                thread.Start();
                _logger.Info($"客户端 {conn.ConnectionInfo.ClientIpAddress} 已有独立扩展屏：{scr.DeviceName} {rect.Width}x{rect.Height}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"给客户端建独立管线失败: {ex.Message}");
                _sessions.TryRemove(conn, out _);
                CloseSession(session);
                return false;
            }
        }
    }

    // ---------------- 槽位记录 ----------------
    // 把"占用了哪个驱动槽位、对应哪块显示器"落盘，下次启动（尤其是崩溃后）就能精确拔屏。
    // 不落盘的话只能盲扫全部槽位，而盲扫会让驱动进入拆除中状态，之后十几秒插不出新屏。

    private static string SlotsPath => Path.Combine(AppContext.BaseDirectory, "vdd_slots.txt");

    private static List<int> LoadSlots()
    {
        var list = new List<int>();
        try
        {
            if (!File.Exists(SlotsPath)) return list;
            foreach (var line in File.ReadAllLines(SlotsPath))
            {
                var parts = line.Split('|');
                if (parts.Length == 0) continue;
                if (int.TryParse(parts[0], out var idx) && idx >= 0 && idx < ParsecVdd.DriverSlots) list.Add(idx);
            }
        }
        catch
        {
        }
        return list;
    }

    /// <summary>必须在 lock(this) 内调用（会枚举 _orphans）。</summary>
    private void SaveSlots()
    {
        try
        {
            var lines = _sessions.Values.Select(s => $"{s.DisplayIndex}|{s.DeviceName}")
                .Concat(_orphans.Select(s => $"{s.DisplayIndex}|{s.DeviceName}"))
                .Distinct()
                .ToArray();
            File.WriteAllLines(SlotsPath, lines);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try { _server.ClientAuthedEvent -= OnAuthed; } catch { }
        try { _server.ClientClosedEvent -= OnClosed; } catch { }
        try { _server.ClientViewportEvent -= OnViewport; } catch { }
        _server.PerClientScreens = false;
        try { _orphanTimer?.Dispose(); } catch { }
        _orphanTimer = null;
        lock (this)
        {
            foreach (var s in _orphans) CloseSession(s);
            _orphans.Clear();
            foreach (var kv in _sessions.ToArray())
            {
                if (_sessions.TryRemove(kv.Key, out var s)) CloseSession(s);
            }
            SaveSlots();   // 全部拔完，记录清空，下次启动不用再清
        }
        try { _vdd.Dispose(); } catch { }
    }
}
