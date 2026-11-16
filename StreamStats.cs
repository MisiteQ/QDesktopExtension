namespace QDesktopExtension;

/// <summary>跨线程共享的运行时统计（管线写入，HTTP/WS 读出）。</summary>
public sealed class StreamStats
{
    public long StartedAtTicks { get; } = Environment.TickCount64;

    private long _framesTotal;
    private long _bytesTotal;
    private long _inputEvents;
    private long _keysTyped;
    private int _fps;
    private int _kbps;
    private volatile bool _capturing = true;

    public bool Capturing => _capturing;

    public void SetCaptureState(bool capturing) => _capturing = capturing;

    public long FramesTotal => Interlocked.Read(ref _framesTotal);
    public long BytesTotal => Interlocked.Read(ref _bytesTotal);
    public long InputEvents => Interlocked.Read(ref _inputEvents);
    public long KeysTyped => Interlocked.Read(ref _keysTyped);
    public int Fps => Volatile.Read(ref _fps);
    public int Kbps => Volatile.Read(ref _kbps);
    public int UptimeSeconds => (int)((Environment.TickCount64 - StartedAtTicks) / 1000);
    public double AvgKbps => UptimeSeconds > 0 ? BytesTotal * 8.0 / 1000 / UptimeSeconds : 0;

    public void AddFrame(int bytes)
    {
        Interlocked.Increment(ref _framesTotal);
        Interlocked.Add(ref _bytesTotal, bytes);
    }

    public void SampleSecond(int frames, long bytes)
    {
        Volatile.Write(ref _fps, frames);
        Volatile.Write(ref _kbps, (int)(bytes * 8 / 1000));
    }

    public void AddInput(int keys = 0)
    {
        Interlocked.Increment(ref _inputEvents);
        if (keys > 0) Interlocked.Add(ref _keysTyped, keys);
    }
}
