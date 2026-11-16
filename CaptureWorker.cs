using System.Diagnostics;

namespace QDesktopExtension;

/// <summary>
/// 独立采集线程 + 双缓冲“最新帧”交接。
/// 目的：GDI 抓屏（尤其高分辨率或带虚拟显示驱动时）单帧要十几到几十毫秒，
/// 如果和编码串行执行，帧率会被两者之和卡死；拆开后吞吐只受较慢的一侧限制。
/// </summary>
public sealed class CaptureWorker
{
    private readonly object _lock = new();
    private readonly Logger _logger;
    private readonly bool _drawCursor;
    private DisplayCapture _capture;
    private Thread? _thread;
    private volatile bool _running;

    private int _published = -1;   // 可被消费的缓冲槽
    private int _lastSlot = -1;    // 最近一次抓完的槽（JPEG 通道按它取快照）
    private int _writing = -1;     // 抓屏线程正在写的槽（JPEG 通道要避开它）
    private readonly int[] _readers = new int[2];   // 每个槽当前有几个消费线程在读（编码线程 + JPEG 线程）

    private volatile int _failures;
    private volatile int _frames;
    private double _lastMs;
    private volatile bool _capturing = true;
    private long _lastWarnTicks;
    private volatile bool _paused;

    public CaptureWorker(DisplayCapture capture, bool drawCursor, Logger logger)
    {
        _capture = capture;
        _drawCursor = drawCursor;
        _logger = logger;
    }

    public DisplayCapture Capture => _capture;

    /// <summary>当前是否可以抓屏（锁屏 / 独占全屏 / 显示模式切换时会变成 false）。</summary>
    public bool Capturing => _capturing;

    /// <summary>上一统计周期内单帧抓取耗时（毫秒）。</summary>
    public double LastCaptureMs => _lastMs;

    /// <summary>
    /// 让采集线程歇着（没人看画面时用）。BitBlt 一帧就要几十毫秒、基本占满一个核，
    /// 白白全速抓屏是空闲时 CPU 居高不下的主要原因。
    /// </summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        // 恢复时别先把暂停前那张旧画面推出去
        if (paused) lock (_lock) { _published = -1; }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "capture" };
        // 抓屏是本机最大的单帧开销，且要和编码线程抢 CPU；稍微提一点优先级能减少被拖长到 3 个 vsync 的概率
        try { _thread.Priority = ThreadPriority.AboveNormal; } catch { }
        _thread.Start();
    }

    public bool Stop()
    {
        _running = false;
        var exited = true;
        try { exited = _thread == null || _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        _thread = null;
        lock (_lock)
        {
            _published = -1;
            _writing = -1;
            _readers[0] = 0;
            _readers[1] = 0;
        }
        return exited;
    }

    /// <summary>只查看最新一帧、不消费（给 JPEG 通道并行读取用）。</summary>
    public bool PeekLatest(out int slot)
    {
        lock (_lock)
        {
            if (_published < 0)
            {
                slot = -1;
                return false;
            }
            slot = _published;
            _readers[slot]++;
            return true;
        }
    }

    /// <summary>
    /// 把最新一帧像素整块拷贝到目标缓冲（JPEG 通道用）。
    /// 必须在锁内拷完：外面读会和抓屏线程写同一块内存，出现撕裂画面。
    /// </summary>
    public bool TryCopyLatest(IntPtr dst, int stride, int height)
    {
        int slot;
        IntPtr src;
        lock (_lock)
        {
            slot = _lastSlot >= 0 ? _lastSlot : _published;
            if (slot < 0) return false;
            // 抓屏线程正在写这块：这一轮先不拷，下一轮再取（硬拷会得到撕裂画面）
            if (_writing == slot) return false;
            src = _capture.BitsOf(slot);
            if (src == IntPtr.Zero) return false;
            // 占住这个槽再出锁：抓屏线程看到 _readers>0 就不会往这块重写，拷完才放
            _readers[slot]++;
        }
        try
        {
            unsafe
            {
                var s = (byte*)src;
                var d = (byte*)dst;
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(s + (long)y * stride, d + (long)y * stride, stride, stride);
            }
            return true;
        }
        finally
        {
            lock (_lock) _readers[slot]--;
        }
    }

    /// <summary>取最新一帧的缓冲槽；没有新帧时最多等 timeoutMs。</summary>
    public bool TryTakeLatest(out int slot, int timeoutMs = 30)
    {
        lock (_lock)
        {
            if (_published < 0)
            {
                Monitor.Wait(_lock, timeoutMs);
                if (_published < 0)
                {
                    slot = -1;
                    return false;
                }
            }
            slot = _published;
            _published = -1;
            _readers[slot]++;
            return true;
        }
    }

    public void Release(int slot)
    {
        lock (_lock)
        {
            if (slot >= 0 && slot < 2 && _readers[slot] > 0) _readers[slot]--;
        }
    }

    private void Loop()
    {
        var lastStat = Stopwatch.GetTimestamp();
        var statFrames = 0;
        long statTicks = 0;

        while (_running)
        {
            if (_paused)
            {
                Thread.Sleep(50);
                continue;
            }

            int slot;
            lock (_lock)
            {
                // 永远抓“既没发布也没被任何消费线程读”的那块；两块都被占就等一帧
                slot = _published == 0 ? 1 : 0;
                if (_readers[slot] > 0 || _published == slot) slot = -1;
                // 先把目标槽占住再出锁：否则 JPEG 线程按 _lastSlot 取快照时，
                // 可能正好选中抓屏线程马上要写的那块，两边同时读写同一片像素
                else
                {
                    _readers[slot]++;
                    _writing = slot;
                }
            }
            if (slot < 0)
            {
                Thread.Sleep(4);
                continue;
            }

            var t0 = Stopwatch.GetTimestamp();
            var ok = _capture.Capture(slot, _drawCursor);
            statTicks += Stopwatch.GetTimestamp() - t0;

            if (ok)
            {
                statFrames++;
                _failures = 0;
                if (!_capturing)
                {
                    _capturing = true;
                    _logger.Info("抓屏已恢复");
                }
                lock (_lock)
                {
                    _lastSlot = slot;
                    _published = slot;
                    _writing = -1;
                    _readers[slot]--;      // 抓取占用解除，之后由消费线程各自加锁读取
                    Monitor.PulseAll(_lock);
                }
            }
            else
            {
                lock (_lock)
                {
                    _writing = -1;
                    _readers[slot]--;
                }
                _failures++;
                _capturing = false;
                // 显示器热插拔 / 模式切换后缓存的屏幕 DC 可能失效：每隔一阵重取一次，别一直卡在失败
                if (_failures % 25 == 0)
                {
                    _capture.RefreshScreenDc();
                    _logger.Info("抓屏持续失败，已重新获取屏幕 DC 重试");
                }
                // 锁屏时每秒会失败几十次，按时间限流，最多 3 秒一条
                var warnNow = Stopwatch.GetTimestamp();
                if (warnNow - _lastWarnTicks >= Stopwatch.Frequency * 3)
                {
                    _lastWarnTicks = warnNow;
                    _logger.Warn("抓屏失败（锁屏 / 独占全屏 / 显示模式切换），继续重试");
                }
                Thread.Sleep(10);
            }

            var now = Stopwatch.GetTimestamp();
            if (now - lastStat >= Stopwatch.Frequency)
            {
                _frames = statFrames;
                _lastMs = statTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, statFrames);
                statFrames = 0;
                statTicks = 0;
                lastStat = now;
            }

            // 抓屏本身可能就是 16~40ms，这里只做轻微礼让，避免空转占满一个核
            if (Stopwatch.GetTimestamp() - t0 < Stopwatch.Frequency / 1000 * 2) Thread.Yield();
        }
    }
}
