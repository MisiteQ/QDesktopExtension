using System.Text;

namespace QDesktopExtension;

/// <summary>按大小滚动、按数量保留的日志器（线程安全）。</summary>
public class Logger
{
    private readonly ConfigModel _cfg;
    private readonly object _lock = new();
    private string _currentLogFile = string.Empty;
    private long _currentFileSize;
    private int _sequence;

    public Logger(ConfigModel cfg)
    {
        _cfg = cfg;
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
        Console.WriteLine(line);
        lock (_lock)
        {
            try
            {
                if (_currentFileSize >= _cfg.LogFileMaxSizeMB * 1024L * 1024L) OpenNewFile();
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                using var fs = new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes);
                _currentFileSize += bytes.Length;
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }
    }

    private void OpenNewFile()
    {
        var now = DateTime.Now;
        var name = $"stream_{now:yyyyMMdd_HHmmss}_{_sequence++:D3}.log";
        _currentLogFile = Path.Combine(_cfg.LogPath, name);
        _currentFileSize = 0;
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
}
