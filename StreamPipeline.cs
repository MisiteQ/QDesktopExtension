using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>采集 → 自适应 → 编码 → 分发 的主循环，采集与编码并行。</summary>
public sealed class StreamPipeline
{
    private readonly ConfigModel _cfg;
    private readonly Logger _logger;
    private readonly VideoEncoder _encoder;
    private readonly AdaptiveController _adaptive;
    private readonly IPipelineHost _host;
    private readonly StreamStats _stats;
    private readonly TouchForwarder _touch;

    // 采集源会在运行期被替换（切换显示器）
    private CaptureWorker _worker;

    // 仅在存在 JPEG 客户端时才创建/使用
    private JpegStreamEncoder? _jpeg;
    private int _jpegW, _jpegH;

    // 仅在存在 MSE 客户端时才创建/使用
    private Fmp4Muxer? _muxer;
    private long _muxStartTicks;
    private int _muxGen = -1;
    private IntPtr _jpegBuf = IntPtr.Zero;   // JPEG 线程自己的像素快照
    private Thread? _jpegThread;             // JPEG 通道线程（停机时要等它退干净再释放缓冲）
    private long _jpegFrames;                // 统计周期内的 JPEG 帧数（纯 JPEG 客户端时用它报统计）
    private long _jpegBytes;

    // 待切换的采集源（显示器 / 窗口）；null = 无请求
    private CaptureSource? _pendingSource;
    private CaptureSource _source;
    private long _lastWindowCheck;
    private VirtualScreen? _virtual;
    // 客户端屏幕分辨率（>0 表示输出固定成这个尺寸，覆盖自适应给出的分辨率）
    private volatile int _forceW;
    private volatile int _forceH;
    private volatile bool _viewPend;   // 客户端改了分辨率，等管线线程重建编码器
    private volatile bool _modeTried;  // 本次请求是否已经尝试过切换虚拟屏原生分辨率
    private long _lastBitRate;         // 最近一次编码码率，改分辨率时沿用，避免画质突变
    private bool _idle;                // 当前是否因为"没人看"而停着采集

    // 切源会释放旧采集缓冲，必须和 JPEG 线程的拷贝互斥，否则可能拷到已释放内存
    private readonly object _switchLock = new();

    public StreamPipeline(ConfigModel cfg, Logger logger, DisplayCapture capture, VideoEncoder encoder,
                          AdaptiveController adaptive, IPipelineHost host, StreamStats stats,
                          TouchForwarder touch, CaptureSource source, VirtualScreen? virtualScreen = null)
    {
        _cfg = cfg;
        _logger = logger;
        _encoder = encoder;
        _adaptive = adaptive;
        _host = host;
        _stats = stats;
        _touch = touch;
        _source = source;
        _virtual = virtualScreen;
        _worker = new CaptureWorker(capture, cfg.DrawCursor, logger);
    }

    /// <summary>
    /// 释放管线自己占用的非托管资源。
    /// JPEG 线程的像素快照是用 Marshal.AllocHGlobal 分配的，不在这里释放的话，
    /// 每"保存并重启服务"一次就漏掉一块（1080p 约 8MB）。
    /// </summary>
    public void Dispose()
    {
        try { _worker.Stop(); } catch { }
        // JPEG 线程可能正往自己的快照缓冲里拷像素：不等它退出就 FreeHGlobal，
        // 会把托管堆写坏（表现为几秒后任意线程莫名 AccessViolation / 进程直接消失）
        var jpegExited = true;
        try { jpegExited = _jpegThread == null || _jpegThread.Join(3000); } catch { }
        _jpegThread = null;
        if (jpegExited)
        {
            lock (_switchLock)
            {
                try { _jpeg?.Dispose(); } catch { }
                _jpeg = null;
                if (_jpegBuf != IntPtr.Zero)
                {
                    try { Marshal.FreeHGlobal(_jpegBuf); } catch { }
                    _jpegBuf = IntPtr.Zero;
                }
            }
        }
        else
        {
            // 线程还卡在编码/拷贝里：此时释放它正在用的 native 对象就是 UAF。
            // 这几个对象宁可漏掉（进程退出时由系统回收），也不能在这里动。
            _logger.Warn("JPEG 通道线程未能在 3 秒内退出，跳过释放 JPEG 编码器与快照缓冲");
        }
        try { _muxer?.Dispose(); } catch { }
        _muxer = null;
        try { _virtual?.Dispose(); } catch { }
        _virtual = null;
    }

    /// <summary>由服务端线程调用，真正的切换在管线线程里完成。</summary>
    public void RequestMonitor(int index) => RequestSource(CaptureSource.Monitor(index));

    public void RequestSource(CaptureSource source) => Interlocked.Exchange(ref _pendingSource, source);

    /// <summary>把某个程序"搬"到扩展屏上（手机上随时可加，最多 4 个）。</summary>
    public bool AddVirtualWindow(IntPtr hwnd, string title)
    {
        if (hwnd == IntPtr.Zero) return false;
        var list = _cfg.VirtualWindows;
        if (list.Any(w => w.Hwnd == hwnd.ToInt64())) return true;
        if (list.Count >= 4)
        {
            _logger.Warn("扩展屏最多放 4 个程序，先移除一个再添加");
            return false;
        }
        var live = WindowSource.GetTitle(hwnd);
        var rc = WindowSource.GetWindowRectOf(hwnd);
        list.Add(new VirtualWindowCfg
        {
            Hwnd = hwnd.ToInt64(),
            Title = live is { Length: > 0 } ? live : title,
            X = rc?.X ?? 0, Y = rc?.Y ?? 0, W = rc?.Width ?? 0, H = rc?.Height ?? 0
        });
        // 像拖到第二块屏一样：把窗口挪出物理屏幕范围，主屏上就看不到它了
        if (list[^1].HasRect) WindowSource.MoveTo(hwnd, ParkX, rc!.Value.Y, rc.Value.Width, rc.Value.Height);
        _logger.Info($"扩展屏投放程序: 「{list[^1].Title}」 共 {list.Count} 个（已移出主屏）");
        _host.VirtualWindowCount = list.Count;
        RequestSource(CaptureSource.VirtualSource());
        return true;
    }

    /// <summary>
    /// 客户端屏幕尺寸上报：这块"新显示器"的分辨率就应该等于对面那块屏。
    /// 扩展屏模式下改画布尺寸；真实显示器模式下直接把编码输出改成这个分辨率，
    /// 客户端就能 1:1 显示、既不裁切也不留黑边。
    /// </summary>
    public void OnClientViewport(int w, int h)
    {
        // 注意：这个回调来自 WebSocket 线程，绝不能在这里动编码器（会和编码线程抢 native 资源，
        // 直接 0xc0000005 崩溃）。只记录目标，真正的重建交给管线线程下一帧执行。
        if (w <= 0 || h <= 0)
        {
            if (_forceW != 0 || _forceH != 0)
            {
                _forceW = 0;
                _forceH = 0;
                _viewPend = true;
                _logger.Info("客户端切回完整显示，分辨率交还自适应");
            }
            return;
        }

        // 面板里的"分辨率上限"是硬约束：客户端上报再大也不越界，且按同一系数缩，避免拉伸；0 = 跟随显示器
        var capW = _cfg.MaxWidth > 0 ? _cfg.MaxWidth : w;
        var capH = _cfg.MaxHeight > 0 ? _cfg.MaxHeight : h;
        var fit = Math.Min(1.0, Math.Min((double)capW / w, (double)capH / h));
        w = Math.Clamp((int)(w * fit) / 2 * 2, 320, 3840);
        h = Math.Clamp((int)(h * fit) / 2 * 2, 240, 2160);

        if (_source.Virtual)
        {
            ResizeVirtual(w, h);
            _forceW = w;
            _forceH = h;
            return;
        }

        if (Math.Abs(_forceW - w) < 16 && Math.Abs(_forceH - h) < 16) return;
        _forceW = w;
        _forceH = h;
        _viewPend = true;
        _logger.Info($"输出分辨率跟随客户端屏幕: {w}x{h}（下一帧生效）");
    }

    /// <summary>按客户端屏幕尺寸调整扩展屏画布（对面这块屏多大，这块扩展屏就多大）。</summary>
    public void ResizeVirtual(int w, int h)
    {
        w = Math.Clamp((w / 2) * 2, 640, 2560);
        h = Math.Clamp((h / 2) * 2, 360, 1600);
        if (Math.Abs(_cfg.VirtualScreenWidth - w) < 24 && Math.Abs(_cfg.VirtualScreenHeight - h) < 24) return;
        _cfg.VirtualScreenWidth = w;
        _cfg.VirtualScreenHeight = h;
        _logger.Info($"扩展屏尺寸跟随客户端: {w}x{h}");
        if (_source.Virtual) RequestSource(CaptureSource.VirtualSource(_cfg.VirtualScreenWidth, _cfg.VirtualScreenHeight));
    }

    /// <summary>清空扩展屏成员（把挪出去的窗口放回原位，屏本身还在）。</summary>
    public void ClearVirtualWindows()
    {
        RestoreVirtualWindows();
        _cfg.VirtualWindows.Clear();
        _logger.Info("扩展屏已清空（窗口已放回原位）");
        _host.VirtualWindowCount = 0;
        if (_source.Virtual) RequestSource(CaptureSource.VirtualSource(_cfg.VirtualScreenWidth, _cfg.VirtualScreenHeight));
    }

    /// <summary>把所有被"搬到扩展屏"的窗口放回原来的位置（清空、停止服务时都要做，免得窗口留在屏幕外找不回来）。</summary>
    public void RestoreVirtualWindows()
    {
        foreach (var w in _cfg.VirtualWindows)
        {
            if (!w.HasRect) continue;
            if (w.Hwnd == 0) continue;
            // 窗口已关掉的，MoveTo 内部会自己判掉
            WindowSource.MoveTo(new IntPtr(w.Hwnd), w.X, w.Y, w.W, w.H);
        }
    }

    /// <summary>物理屏幕右边缘之外的停放坐标。</summary>
    private static int ParkX
    {
        get
        {
            var right = 0;
            foreach (var s in System.Windows.Forms.Screen.AllScreens) right = Math.Max(right, s.Bounds.Right);
            return right + 60;
        }
    }

    public void Run(CancellationToken token)
    {
        var period = Math.Max(1, Stopwatch.Frequency / _cfg.Fps);
        var nextTick = Stopwatch.GetTimestamp();
        var lastStat = nextTick;
        var frameIndex = 0;
        var statFrames = 0;
        long statBytes = 0;
        long encodeTicks = 0;

        _worker.Start();

        // JPEG 通道独立成线程：它只读最新抓屏帧，不和 H.264 主链路抢同一个节奏，
        // 这样手机端能跑 20fps 静态帧而主链路仍是 30fps
        var jpegThread = new Thread(() => RunJpegLoop(token)) { IsBackground = true, Name = "jpeg" };
        _jpegThread = jpegThread;
        jpegThread.Start();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var requested = Interlocked.Exchange(ref _pendingSource, null);
                if (requested != null) SwitchSource(requested);

                // 客户端上报了新分辨率：在管线线程里重建编码器（跨线程直接重建会崩 native 编码器）
                if (_viewPend) ApplyViewportTarget();

                // 窗口模式：窗口被拖动/缩放/关掉时要跟着重建，否则画面会裁切或整片黑
                // 虚拟屏模式：成员窗口全关掉时退回显示器，别让客户端一直黑屏
                if (_source.IsWindow || _source.Virtual)
                {
                    var nowTicks = Stopwatch.GetTimestamp();
                    if (nowTicks - _lastWindowCheck >= Stopwatch.Frequency)
                    {
                        _lastWindowCheck = nowTicks;
                        if (_source.Virtual)
                        {
                            // 扩展屏允许是一个空屏（就像新接的显示器还没放窗口），
                            // 所以这里不做任何回退；成员被关掉后由 1 秒一次的 RecomputeTiles 自行剔除。
                        }
                        else
                        {
                            var rect = WindowSource.GetClientScreenRect(_source.Hwnd);
                            if (rect == null)
                            {
                                _logger.Warn($"窗口「{_source.Title}」已关闭，回退到显示器 {_cfg.CaptureMonitor}");
                                SwitchSource(CaptureSource.Monitor(_cfg.CaptureMonitor));
                            }
                            else if (rect.Value.Width != _worker.Capture.Width || rect.Value.Height != _worker.Capture.Height)
                            {
                                _logger.Info($"窗口尺寸变化({rect.Value.Width}x{rect.Value.Height})，重建采集与编码");
                                SwitchSource(_source);
                            }
                        }
                    }
                }

                // 没人等这条管线的画面（客户端都拿到了自己的扩展屏 / 已经断开）：
                // 停抓停编。GDI 抓一帧 2240x1400 要 50~90ms，全速空抓等于常年占满一个核。
                if (_host.Consumers <= 0)
                {
                    if (!_idle)
                    {
                        _idle = true;
                        _worker.SetPaused(true);
                        _logger.Info($"{_source.Describe()} 没有客户端在看，暂停采集与编码");
                    }
                    nextTick = Stopwatch.GetTimestamp();
                    Thread.Sleep(80);
                    continue;
                }
                if (_idle)
                {
                    _idle = false;
                    _worker.SetPaused(false);
                    _logger.Info($"{_source.Describe()} 有客户端接入，恢复采集与编码");
                }

                // 取最新帧；抓屏失败（锁屏等）时没有新帧，这一轮就什么都不编码，避免推陈旧画面
                if (_worker.TryTakeLatest(out var slot, 30))
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now < nextTick)
                    {
                        // 采集比目标帧率更快：丢掉这帧，保持输出节奏（实时性优先，不补帧）
                        _worker.Release(slot);
                        Thread.Sleep(2);
                    }
                    else if (_host.VideoClients <= 0)
                    {
                        // 客户端只吃 JPEG（手机兼容通道）：H.264 编出来没人接，别再白烧 CPU
                        _worker.Release(slot);
                        nextTick += period;
                        var cur = Stopwatch.GetTimestamp();
                        if (cur - nextTick > period * 3) nextTick = cur;
                    }
                    else
                    {
                        var forceKey = _host.ConsumeKeyFrameRequest() ||
                                       frameIndex % Math.Max(1, _cfg.Fps * _cfg.KeyFrameIntervalSec) == 0;

                        long frameBytes = 0;
                        var t1 = Stopwatch.GetTimestamp();
                        var encoded = _encoder.Encode(_worker.Capture.BitsOf(slot), _worker.Capture.Stride, forceKey);
                        encodeTicks += Stopwatch.GetTimestamp() - t1;

                        _worker.Release(slot);

                        foreach (var frame in encoded)
                        {
                            frameBytes += frame.Data.Length;
                            statBytes += frame.Data.Length;
                            statFrames++;
                            _stats.AddFrame(frame.Data.Length);
                            _host.BroadcastFrame(frame.Data, frame.KeyFrame);

                            // MSE 客户端（手机自带浏览器）：封装成 fMP4 分片单独推一路
                            if (_host.MseClients > 0) FeedMuxer(frame);
                        }

                        frameIndex++;

                        if (encoded.Count > 0)
                        {
                            var target = _adaptive.Feed(frameBytes);
                            if (target.HasValue)
                            {
                                var fw = _forceW;
                                var fh = _forceH;
                                // 客户端报过屏幕尺寸就锁定输出分辨率（分辨率跟着对面那块屏走）
                                if (fw > 0 && fh > 0) Reconfigure(new VideoTarget(fw, fh, target.Value.BitRate));
                                else Reconfigure(target.Value);
                            }
                        }

                        nextTick += period;
                        var current = Stopwatch.GetTimestamp();
                        if (current - nextTick > period * 3) nextTick = current;
                    }
                }

                // 统计每秒刷一次（抓屏失败时也要刷，否则 /status 会停在旧值）
                _stats.SetCaptureState(_worker.Capturing);
                var after = Stopwatch.GetTimestamp();
                if (after - lastStat >= Stopwatch.Frequency)
                {
                    // 纯 JPEG 客户端时 H.264 没在编，统计改报 JPEG 通道，否则客户端会看到 0 fps
                    if (_host.VideoClients <= 0)
                    {
                        statFrames = (int)Interlocked.Exchange(ref _jpegFrames, 0);
                        statBytes = (int)Interlocked.Exchange(ref _jpegBytes, 0);
                    }
                    _stats.SampleSecond(statFrames, statBytes);
                    _host.PushStats(statFrames, (int)(statBytes * 8 / 1000), _worker.Capturing);
                    var encMs = encodeTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, statFrames);
                    _logger.Info($"运行中: {_stats.Fps} fps, {_stats.Kbps} kbps, {_encoder.Width}x{_encoder.Height}, " +
                                 $"{_source.Describe()}, 客户端 {_host.AuthedClients}, " +
                                 $"采集 {_worker.LastCaptureMs:F1}ms 编码 {encMs:F1}ms" +
                                 (_worker.Capturing ? "" : "（抓屏不可用）"));
                    statFrames = 0;
                    statBytes = 0;
                    encodeTicks = 0;
                    lastStat = after;
                }
            }
        }
        finally
        {
            // 先等 JPEG 线程退出：它可能正拿着 _jpeg 编码、往 _jpegBuf 里拷像素，
            // 不等它退干净就释放 native 编码器/缓冲，进程会直接崩（0xc0000005）
            var jpegExited = true;
            try { jpegExited = _jpegThread == null || _jpegThread.Join(3000); } catch { }
            // 停机前把搬到扩展屏的窗口放回原位，别让它们留在屏幕外
            try { RestoreVirtualWindows(); } catch { }
            _worker.Stop();
            if (jpegExited)
            {
                try { _jpeg?.Dispose(); } catch { }
                _jpeg = null;
            }
            else
            {
                // 线程还活着：释放它手里的 native 对象会让它在下一秒踩空，宁可留给进程回收
                _logger.Warn("JPEG 通道线程退出超时，保留其编码器与快照缓冲不释放");
            }
            try { _muxer?.Dispose(); } catch { }
            _muxer = null;
        }
    }

    /// <summary>存在 MSE 客户端时，把 AVCC 帧封成 fMP4 分片推给它们。</summary>
    private void FeedMuxer(EncodedFrame frame)
    {
        try
        {
            // 有新的 MSE 客户端接入（代次变化）就必须重开一路封装器：
            // 新客户端只拿到从当前时刻往后的分片，沿用旧封装器会让它从没有关键帧的位置开始，永远解不出第一帧
            var gen = _host.MseGeneration;
            if (_muxer != null && gen != _muxGen)
            {
                _muxer.Dispose();
                _muxer = null;
            }
            _muxGen = gen;

            if (_muxer == null)
            {
                var avcC = _encoder.Extradata;
                if (avcC == null) return;          // 非 H.264 或还没准备好
                // 新封装器必须从关键帧开始，否则 MSE 解不出第一帧
                _host.RequestKeyFrame();
                if (!frame.KeyFrame) return;

                _muxer = new Fmp4Muxer();
                _muxer.Init(_encoder.Width, _encoder.Height, avcC, _cfg.Fps);
                _muxStartTicks = Stopwatch.GetTimestamp();
                _host.SetMseInit(_muxer.InitSegment!);
                _logger.Info("fMP4 通道就绪（MSE 客户端可用 30fps H.264）");
            }

            // 用真实墙钟做媒体时间戳：实际帧率掉到 20fps 时，媒体时钟也必须按真实时间走，
            // 否则手机端播放点会越落越远（"延迟越来越严重"就是这个原因）
            var ms = (long)((Stopwatch.GetTimestamp() - _muxStartTicks) * 1000.0 / Stopwatch.Frequency);
            _muxer.Write(frame.Data, frame.KeyFrame, ms);
            var chunk = _muxer.TakePending();
            if (chunk != null) _host.BroadcastFmp4(chunk);
        }
        catch (Exception ex)
        {
            _logger.Warn($"fMP4 封装失败，关闭该通道: {ex.Message}");
            _muxer?.Dispose();
            _muxer = null;
        }
    }

    /// <summary>JPEG 兼容通道：独立线程，按 JpegFps（默认 20）推静态帧，只读最新抓屏帧。</summary>
    private void RunJpegLoop(CancellationToken token)
    {
        var fps = Math.Max(1, _cfg.JpegFps);
        var period = Math.Max(1, Stopwatch.Frequency / fps);
        var nextTick = Stopwatch.GetTimestamp();
        var lastDiag = Stopwatch.GetTimestamp();

        while (!token.IsCancellationRequested)
        {
            if (_host.JpegClients <= 0)
            {
                if (_jpeg != null)
                {
                    try { _jpeg.Dispose(); } catch { }
                    _jpeg = null;
                }
                Thread.Sleep(30);
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            if (now < nextTick)
            {
                Thread.Sleep(2);
                continue;
            }

            try
            {
                // 与 SwitchSource 互斥：避免拷到已经被释放的旧采集缓冲
                lock (_switchLock)
                {
                    var capture = _worker.Capture;
                    if (_jpeg == null || _jpegW != capture.Width || _jpegH != capture.Height)
                    {
                        _jpeg?.Dispose();
                        var w = Math.Min(_cfg.JpegWidth, capture.Width);
                        var h = Math.Max(2, (int)Math.Round(capture.Height * (double)w / capture.Width));
                        _jpeg = new JpegStreamEncoder();
                        _jpeg.Init(capture.Width, capture.Height, w, h);
                        _jpegW = capture.Width;
                        _jpegH = capture.Height;
                        // JPEG 线程需要一份自己的像素快照，避免和主编码线程抢同一块缓冲
                        if (_jpegBuf != IntPtr.Zero) Marshal.FreeHGlobal(_jpegBuf);
                        _jpegBuf = Marshal.AllocHGlobal(capture.Stride * capture.Height);
                        _logger.Info($"JPEG 兼容通道就绪: {w}x{h} @ {fps}fps");
                    }

                    if (_worker.TryCopyLatest(_jpegBuf, capture.Stride, capture.Height))
                    {
                        var jpeg = _jpeg.Encode(_jpegBuf, capture.Stride);
                        if (jpeg != null)
                        {
                            _host.BroadcastJpeg(jpeg);
                            Interlocked.Increment(ref _jpegFrames);
                            Interlocked.Add(ref _jpegBytes, jpeg.Length);
                        }
                        else _logger.Warn("JPEG 编码返回空（MJPEG 编码失败）");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"JPEG 通道失败: {ex.Message}");
                try { _jpeg?.Dispose(); } catch { }
                _jpeg = null;
            }

            // 每 5 秒打一条诊断，避免"通道静默不工作"时无从下手
            var diagNow = Stopwatch.GetTimestamp();
            if (diagNow - lastDiag >= Stopwatch.Frequency * 5)
            {
                lastDiag = diagNow;
                _logger.Info($"JPEG 通道诊断: 客户端 {_host.JpegClients}, 编码器 {(_jpeg != null ? "就绪" : "未建")}, 目标 {fps}fps");
            }

            nextTick += period;
            var after = Stopwatch.GetTimestamp();
            if (after - nextTick > period * 3) nextTick = after;
        }
    }

    /// <summary>把客户端上报的分辨率应用到编码器（只能在管线线程里调用）。</summary>
    private void ApplyViewportTarget()
    {
        _viewPend = false;
        try
        {
            var fw = _forceW;
            var fh = _forceH;
            if (fw > 0 && fh > 0)
            {
                // 先把虚拟显示器本身切到客户端的原生分辨率：这样采集、编码、显示都是同一尺寸，
                // 不需要任何拉伸或裁切。做不到（不是虚拟屏 / 驱动拒绝）时再退回等比缩放。
                if (!_modeTried)
                {
                    _modeTried = true;
                    if (TrySetNativeMode(fw, fh))
                    {
                        // 显示器尺寸变了，采集器要按新模式重开；下一帧再按新尺寸重建编码器
                        _pendingSource = _source;
                        _viewPend = true;
                        return;
                    }
                }
                Reconfigure(new VideoTarget(fw, fh, _lastBitRate > 0 ? _lastBitRate : _cfg.BitRate));
            }
            else
            {
                var cap = _worker.Capture;
                var cw = Math.Min(cap.Width, _cfg.MaxWidth > 0 ? _cfg.MaxWidth : cap.Width);
                var ch = Math.Max(2, (int)Math.Round(cap.Height * (double)cw / cap.Width));
                Reconfigure(new VideoTarget(cw, ch, _cfg.BitRate));
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"应用客户端分辨率失败: {ex.Message}");
        }
    }

    /// <summary>把虚拟显示器切到 w×h 的原生模式（需要时弹一次 UAC 添加模式）。</summary>
    private bool TrySetNativeMode(int w, int h)
    {
        try
        {
            if (_source.IsWindow || _source.Virtual) return false;
            var idx = _source.MonitorIndex;
            if (idx < 0) return false;
            var dir = AppContext.BaseDirectory;
            return VirtualDisplayMode.EnsureMode(idx, w, h, dir, _logger);
        }
        catch (Exception ex)
        {
            _logger.Warn($"切换原生分辨率失败: {ex.Message}");
            return false;
        }
    }

    private void Reconfigure(VideoTarget target)
    {
        try
        {
            var capture = _worker.Capture;
            _lastBitRate = target.BitRate;
            _encoder.Init(target.Width, target.Height, capture.Width, capture.Height, _cfg.Fps, target.BitRate);
            _host.UpdateVideoInfo(_encoder);
            try { _muxer?.Dispose(); } catch { }
            _muxer = null;                        // 编码参数变了，fMP4 头要重做
            _host.RequestKeyFrame();
            _logger.Info($"编码器已重建: {target.Width}x{target.Height} @ {target.BitRate / 1000} kbps");
        }
        catch (Exception ex)
        {
            _logger.Error($"重建编码器失败，保持原参数: {ex.Message}");
        }
    }

    /// <summary>切换采集源（显示器 / 窗口）：重建 capture / 输入映射 / 自适应目标 / 编码器，并通知客户端。</summary>
    private void SwitchSource(CaptureSource requested)
    {
        // 越界或显示器数量变化时，落到有效序号上，避免状态里记着一个不存在的屏幕
        var effective = requested;
        if (!effective.IsWindow && !effective.Virtual)
        {
            var count = DisplayCapture.GetMonitors().Count;
            var idx = effective.MonitorIndex;
            if (idx >= count) effective = CaptureSource.Monitor(0);
            else if (idx < -1) effective = CaptureSource.Monitor(0);
        }

        VirtualScreen? newVirtual = null;
        DisplayCapture newCapture;
        try
        {
            if (effective.Virtual)
            {
                newVirtual = VirtualScreen.Build(_cfg, _logger);
                _logger.Info($"扩展屏成员: {string.Join(" | ", newVirtual.Tiles.Select(t => t.Title))}"
                             + (newVirtual.WindowCount == 0 ? "（空屏，等手机上投放程序）" : ""));
            }
            newCapture = effective.CreateCapture(newVirtual);
        }
        catch (Exception ex)
        {
            _logger.Error($"切换采集源失败（{effective.Describe()}）: {ex.Message}");
            newVirtual?.Dispose();
            return;
        }

        var rect = newCapture.Rect;
        // 扩展屏的状态要报真实画布尺寸（可被客户端缩放），不能永远是 1920×1080
        if (effective.Virtual) effective = effective with { VirtualWidth = newCapture.Width, VirtualHeight = newCapture.Height };
        DisplayCapture old;
        VirtualScreen? oldVirtual;
        // 和 JPEG 线程互斥：它可能正拿着旧缓冲的指针在拷像素
        lock (_switchLock)
        {
            var oldWorker = _worker;
            oldWorker.Stop();             // 先停采集线程，保证没有人还在写旧缓冲
            old = oldWorker.Capture;
            oldVirtual = _virtual;
            _worker = new CaptureWorker(newCapture, _cfg.DrawCursor, _logger);
            _worker.Start();
            try { old.Dispose(); } catch { }
            _virtual = newVirtual;
        }
        try { oldVirtual?.Dispose(); } catch { }

        // 输入坐标映射必须跟着采集区域走，否则远程点击会落到别的位置
        if (newVirtual != null) _touch.SetVirtualScreen(newVirtual);
        else _touch.SetBounds(rect);

        _adaptive.UpdateSourceSize(newCapture.Width, newCapture.Height);
        _source = effective;
        _host.CurrentSource = effective;
        _host.VirtualWindowCount = newVirtual?.WindowCount ?? _cfg.VirtualWindows.Count;
        _host.CurrentMonitor = effective.IsWindow ? -2 : effective.Virtual ? -3 : effective.MonitorIndex;

        // 记住这次选择，重启后还能回到同一个窗口 / 同一套虚拟屏
        _cfg.CaptureWindow = effective.IsWindow ? effective.Hwnd.ToInt64() : 0;
        _cfg.CaptureWindowTitle = effective.IsWindow ? effective.Title : "";
        _cfg.UseVirtualScreen = effective.Virtual;
        if (!effective.IsWindow && !effective.Virtual) _cfg.CaptureMonitor = effective.MonitorIndex;

        var target = _adaptive.Current;
        try
        {
            _encoder.Init(target.Width, target.Height, newCapture.Width, newCapture.Height, _cfg.Fps, target.BitRate);
            _host.UpdateVideoInfo(_encoder);
            try { _muxer?.Dispose(); } catch { }
            _muxer = null;
            _host.RequestKeyFrame();
            _logger.Info($"已切换采集源: {effective.Describe()} 区域={rect} 编码={target.Width}x{target.Height}");
        }
        catch (Exception ex)
        {
            _logger.Error($"切换采集源后重建编码器失败: {ex.Message}");
        }
    }
}
