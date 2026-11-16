namespace QDesktopExtension;

public class TrafficStat
{
    private readonly object _lock = new();
    private long _tcpBytes;

    public void AddTcp(long b) { lock (_lock) _tcpBytes += b; }

    public long SampleAndReset()
    {
        lock (_lock)
        {
            var t = _tcpBytes;
            _tcpBytes = 0;
            return t;
        }
    }
}
