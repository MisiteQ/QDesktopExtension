using Fleck;

namespace QDesktopExtension;

/// <summary>
 /// 推流管线的出口。默认实现把每一帧广播给所有客户端（= 现在的单屏共享行为）；
 /// 每个客户端自己的管线则用 <see cref="ClientPipelineHost"/>，只发给这一个连接。
 /// </summary>
public interface IPipelineHost
{
    void BroadcastFrame(byte[] data, bool keyFrame);
    void BroadcastJpeg(byte[] jpeg);
    void BroadcastFmp4(byte[] fragment);
    void SetMseInit(byte[] init);
    void UpdateVideoInfo(VideoEncoder encoder);
    void RequestKeyFrame();
    bool ConsumeKeyFrameRequest();
    /// <summary>把这条管线自己的帧率/码率/抓屏状态推给客户端（共享管线由服务端统一广播）。</summary>
    void PushStats(int fps, int kbps, bool capturing);
    int AuthedClients { get; }
    /// <summary>真正在等这条管线画面的人数：0 说明抓屏+编码纯属白烧 CPU，可以停下来。</summary>
    int Consumers { get; }
    /// <summary>在等 H.264/MSE 画面的客户端数：全是 JPEG 客户端时就不用再编 H.264 了。</summary>
    int VideoClients { get; }
    int JpegClients { get; }
    int MseClients { get; }
    int MseGeneration { get; }
    CaptureSource CurrentSource { get; set; }
    int CurrentMonitor { get; set; }
    int VirtualWindowCount { get; set; }
}

/// <summary>现有行为：所有帧广播给全部客户端。</summary>
public sealed class ServerPipelineHost : IPipelineHost
{
    private readonly StreamServer _server;
    public ServerPipelineHost(StreamServer server) => _server = server;

    public void BroadcastFrame(byte[] data, bool keyFrame) => _server.BroadcastFrame(data, keyFrame);
    public void BroadcastJpeg(byte[] jpeg) => _server.BroadcastJpeg(jpeg);
    public void BroadcastFmp4(byte[] fragment) => _server.BroadcastFmp4(fragment);
    public void SetMseInit(byte[] init) => _server.SetMseInit(init);
    public void UpdateVideoInfo(VideoEncoder encoder) => _server.UpdateVideoInfo(encoder);
    public void RequestKeyFrame() => _server.RequestKeyFrame();
    public bool ConsumeKeyFrameRequest() => _server.ConsumeKeyFrameRequest();
    // 共享管线的统计由服务端看门狗统一广播，这里不重复推
    public void PushStats(int fps, int kbps, bool capturing) { }
    public int AuthedClients => _server.AuthedClients;
    public int Consumers => _server.SharedClients;
    public int VideoClients => _server.SharedVideoClients;
    public int JpegClients => _server.JpegClients;
    public int MseClients => _server.MseClients;
    public int MseGeneration => _server.MseGeneration;
    public CaptureSource CurrentSource { get => _server.CurrentSource; set => _server.CurrentSource = value; }
    public int CurrentMonitor { get => _server.CurrentMonitor; set => _server.CurrentMonitor = value; }
    public int VirtualWindowCount { get => _server.VirtualWindowCount; set => _server.VirtualWindowCount = value; }
}

/// <summary>一客户端一条管线：帧、参数、关键帧请求都只作用于这一个连接。</summary>
public sealed class ClientPipelineHost : IPipelineHost
{
    private readonly StreamServer _server;
    // 同一台设备重连时，这条管线直接转交给新连接，不再重建采集/编码/虚拟屏
    private volatile IWebSocketConnection _conn;

    private readonly object _lock = new();
    private bool _keyFrameRequested = true;
    private int _w = 1280, _h = 720;

    public ClientPipelineHost(StreamServer server, IWebSocketConnection conn)
    {
        _server = server;
        _conn = conn;
    }

    /// <summary>把这条管线转接到新连接（设备重连时用）。</summary>
    public void Retarget(IWebSocketConnection conn) => _conn = conn;

    /// <summary>
    /// 发帧前消耗带宽配额（与共享管线同一份限流）。
    /// 超出 ClientMaxBitRate 时丢帧，而不是绕过限制直接塞满客户端链路。
    /// ACL 调用失败时放行，宁可多给也不让画面卡死。
    /// </summary>
    private bool Quota(long bytes)
    {
        try { return _server.TryConsumeClientQuota(_conn.ConnectionInfo.ClientIpAddress, bytes); }
        catch { return true; }
    }

    public void BroadcastFrame(byte[] data, bool keyFrame)
    {
        var packet = new byte[data.Length + 1];
        packet[0] = keyFrame ? (byte)1 : (byte)0;
        Buffer.BlockCopy(data, 0, packet, 1, data.Length);
        if (!Quota(packet.Length)) return;
        _server.SendBinary(_conn, packet);
    }

    public void BroadcastJpeg(byte[] jpeg)
    {
        var packet = new byte[jpeg.Length + 1];
        packet[0] = 2;
        Buffer.BlockCopy(jpeg, 0, packet, 1, jpeg.Length);
        if (!Quota(packet.Length)) return;
        _server.SendBinary(_conn, packet);
    }
    public void BroadcastFmp4(byte[] fragment)
    {
        var packet = new byte[fragment.Length + 1];
        packet[0] = 3;
        Buffer.BlockCopy(fragment, 0, packet, 1, fragment.Length);
        if (!Quota(packet.Length)) return;
        _server.SendBinary(_conn, packet);
    }

    public void SetMseInit(byte[] init) => _server.SendMseInit(_conn, init);

    public void UpdateVideoInfo(VideoEncoder encoder)
    {
        lock (_lock) { _w = encoder.Width; _h = encoder.Height; }
        var codec = encoder.CodecString();
        var desc = Convert.ToBase64String(encoder.Extradata ?? Array.Empty<byte>());
        _server.SendText(_conn, $"{{\"type\":\"video_info\",\"codec\":\"{codec}\",\"width\":{_w},\"height\":{_h},\"description\":\"{desc}\"}}");
    }

    public void RequestKeyFrame() => _keyFrameRequested = true;

    public bool ConsumeKeyFrameRequest()
    {
        if (!_keyFrameRequested) return false;
        _keyFrameRequested = false;
        return true;
    }

    // 独立屏的帧率/抓屏状态只跟这条管线有关，和共享画面不同，必须单独告诉客户端
    public void PushStats(int fps, int kbps, bool capturing) =>
        _server.SendText(_conn, $"{{\"type\":\"stats\",\"fps\":{fps},\"kbps\":{kbps},\"clients\":1," +
                                $"\"capturing\":{(capturing ? "true" : "false")}}}");

    // 这条管线只服务一个连接：JPEG / MSE 都按这个连接真实的通道来判断
    public int AuthedClients => 1;
    public int Consumers => _conn.IsAvailable ? 1 : 0;
    public int VideoClients => _conn.IsAvailable && !_server.IsJpegClient(_conn) ? 1 : 0;
    public int JpegClients => _server.IsJpegClient(_conn) ? 1 : 0;
    public int MseClients => _server.IsMseClient(_conn) ? 1 : 0;
    public int MseGeneration => 1;   // 固定代次：本管线的 fMP4 封装器只建一次
    public CaptureSource CurrentSource { get; set; } = null!;
    public int CurrentMonitor { get; set; } = -1;
    public int VirtualWindowCount { get; set; }
}
