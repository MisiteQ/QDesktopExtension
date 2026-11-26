using System.Text;

namespace QDesktopExtension;

/// <summary>按大小滚动、按数量保留的日志器（线程安全）。</summary>
public class Logger : IDisposable
{
    private readonly ConfigModel _cfg;
    private readonly object _lock = new();
    private readonly bool _toConsole;
    private string _currentLogFile = string.Empty;
    private long _currentFileSize;
    private int _sequence;
    private FileStream? _stream;
    private bool _disposed;

    /// <param name="cfg">配置（提供日志路径、级别、滚动策略）。</param>
    /// <param name="toConsole">是否同时输出到控制台。仅控制台模式需要，GUI/托盘模式保持静默。</param>
    public Logger(ConfigModel cfg, bool toConsole = false)
    {
        _cfg = cfg;
        _toConsole = toConsole;
        Directory.CreateDirectory(_cfg.LogPath);
        OpenNewFile();
    }

    public void Debug(string msg) => Write(LogLevel.Debug, msg);
    public void Info(string msg) => Write(LogLevel.Info, msg);
    public void Warn(string msg) => Write(LogLevel.Warn, msg);
    public void Error(string msg) => Write(LogLevel.Error, msg);

    private void Write(LogLevel level, string msg)
    {
        // 枚举没有定义关系运算符，必须比较底层值
        if ((int)level < (int)_cfg.LogLevel) return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";

        // 控制台输出只在控制台模式下做，GUI/托盘模式保持静默
        if (_toConsole)
        {
            try { Console.WriteLine(line); } catch { }
        }

        lock (_lock)
        {
            try
            {
                if (_disposed) return;
                // 常驻文件流：避免每条日志开关文件流的开销
                if (_stream == null) OpenNewFile();
                if (_currentFileSize >= _cfg.LogFileMaxSizeMB * 1024L * 1024L) OpenNewFile();
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                _stream!.Write(bytes);
                // 及时刷盘：常驻流有内部缓冲，不 Flush 则日志会积累在内存、文件长时间为空，
                // 影响用户排查问题。日志非最热路径，每条 Flush 的开销可接受。
                _stream.Flush();
                _currentFileSize += bytes.Length;
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    /// <summary>开启一个新日志文件：关闭旧常驻流并清空滚动计数。</summary>
    private void OpenNewFile()
    {
        // 关闭旧流（可能为 null）
        var old = _stream;
        _stream = null;
        if (old != null)
        {
            try { old.Dispose(); } catch { }
        }

        var now = DateTime.Now;
        var name = $"stream_{now:yyyyMMdd_HHmmss}_{_sequence++:D3}.log";
        _currentLogFile = Path.Combine(_cfg.LogPath, name);
        _currentFileSize = 0;
        // 以追加模式常驻打开，避免每条日志开关文件流
        try
        {
            _stream = new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }
        catch
        {
            // 打不开就保持 _stream 为 null，后续 Write 会再次尝试
        }
        CleanOldLogs();
    }

    private void CleanOldLogs()
    {
        try
        {
            var files = Directory.GetFiles(_cfg.LogPath, "stream_*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(_cfg.LogFileCount)
                .ToList();
            foreach (var f in files) f.Delete();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            try { _stream?.Dispose(); } catch { }
            _stream = null;
        }
    }
}
