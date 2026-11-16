namespace QDesktopExtension;

public readonly record struct VideoTarget(int Width, int Height, long BitRate);

/// <summary>
/// 基于编码后帧大小的码率自适应：窗口采样 + 冷却时间 + 迟滞，避免来回抖动。
/// 只给出目标参数，真正重建编码器由 StreamPipeline 执行。
/// </summary>
public class AdaptiveController
{
    private readonly ConfigModel _cfg;
    private readonly Logger _logger;
    private int _sourceWidth;
    private int _sourceHeight;

    private long _currentBitrate;
    private long _ceilingBitrate;
    private int _currentW, _currentH;

    private const int WindowFrames = 30;
    private int _frameCount;
    private long _sumBytes;
    private long _lastChangeTicks;

    public AdaptiveController(ConfigModel cfg, Logger logger, int sourceW, int sourceH, long initBr)
    {
        _cfg = cfg;
        _logger = logger;
        _sourceWidth = sourceW;
        _sourceHeight = sourceH;
        _currentBitrate = initBr;
        // 向上恢复只回到用户配置的目标码率，不再无脑冲到 MaxBitRate
        _ceilingBitrate = Math.Min(cfg.MaxBitRate, Math.Max(cfg.MinBitRate, cfg.BitRate));
        var start = ClampResolution(sourceW, sourceH, initBr);
        _currentW = start.Width;
        _currentH = start.Height;
        _lastChangeTicks = Environment.TickCount64;
    }

    public VideoTarget Current => new(_currentW, _currentH, _currentBitrate);

    /// <summary>切换采集源（换显示器）后重新计算目标分辨率。</summary>
    public void UpdateSourceSize(int sourceW, int sourceH)
    {
        _sourceWidth = sourceW;
        _sourceHeight = sourceH;
        var target = ClampResolution(sourceW, sourceH, _currentBitrate);
        _currentW = target.Width;
        _currentH = target.Height;
        _frameCount = 0;
        _sumBytes = 0;
        _lastChangeTicks = Environment.TickCount64;
    }

    /// <summary>喂入一帧的编码后字节数，返回需要切换的新目标；无需切换时返回 null。</summary>
    public VideoTarget? Feed(long frameBytes)
    {
        if (!_cfg.EnableAdaptive) return null;
        _sumBytes += frameBytes;
        _frameCount++;
        if (_frameCount < WindowFrames) return null;

        var avg = _sumBytes / _frameCount;
        _frameCount = 0;
        _sumBytes = 0;

        var now = Environment.TickCount64;
        if (now - _lastChangeTicks < 2000) return null;   // 冷却，避免抖动

        if (_cfg.Fps <= 0) return null;
        var budget = _currentBitrate / _cfg.Fps;
        var changed = false;

        if (avg > budget * 1.15 && _currentBitrate > _cfg.MinBitRate)
        {
            _currentBitrate = Math.Max(_cfg.MinBitRate, (long)(_currentBitrate * 0.85));
            changed = true;
            _logger.Info($"自适应：帧偏大(avg={avg / 1024}KB)，码率降到 {_currentBitrate / 1000} kbps");
        }
        else if (avg < budget * 0.6 && avg > budget * 0.05 && _currentBitrate < _ceilingBitrate)
        {
            // 只有"确实在编码、但没吃满预算"才向上恢复；静止画面 avg≈0，不该触发重建
            _currentBitrate = Math.Min(_ceilingBitrate, (long)(_currentBitrate * 1.15));
            changed = true;
            _logger.Info($"自适应：码率余量充足(avg={avg / 1024}KB)，恢复到 {_currentBitrate / 1000} kbps");
        }

        if (changed)
        {
            var target = ClampResolution(_sourceWidth, _sourceHeight, _currentBitrate);
            if (target.Width != _currentW || target.Height != _currentH)
            {
                _logger.Info($"自适应：分辨率 {_currentW}x{_currentH} -> {target.Width}x{target.Height}");
            }
            _currentW = target.Width;
            _currentH = target.Height;
            _lastChangeTicks = now;
            return new VideoTarget(_currentW, _currentH, _currentBitrate);
        }
        return null;
    }

    /// <summary>按码率档位选分辨率，且不超过采集源与配置上限。宽高始终按同一系数缩放，避免超宽屏/竖屏被拉伸。</summary>
    private (int Width, int Height) ClampResolution(int srcW, int srcH, long bitrate)
    {
        double scale;
        if (bitrate < 3_000_000) scale = 0.5;
        else if (bitrate < 6_000_000) scale = 0.75;
        else scale = 1.0;

        // 上限允许的等比系数：不能单独压宽或压高；0 = 跟随显示器原生分辨率
        var capW = _cfg.MaxWidth > 0 ? _cfg.MaxWidth : srcW;
        var capH = _cfg.MaxHeight > 0 ? _cfg.MaxHeight : srcH;
        var fit = Math.Min(1.0, Math.Min((double)capW / srcW, (double)capH / srcH));
        // 下限允许的等比系数（采集源比下限还小时不放大）
        var floor = Math.Min(fit, Math.Max((double)_cfg.MinWidth / srcW, (double)_cfg.MinHeight / srcH));
        var total = Math.Clamp(fit * scale, floor, fit);

        var w = Even(Math.Max(2, (int)Math.Round(srcW * total)));
        var h = Even(Math.Max(2, (int)Math.Round(srcH * total)));
        return (w, h);
    }

    private static int Even(int v) => v % 2 == 0 ? v : Math.Max(2, v - 1);
}
