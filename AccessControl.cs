using System.Diagnostics;
using System.Net;

namespace QDesktopExtension;

/// <summary>IP 黑白名单 + 单客户端带宽配额（每秒窗口）。</summary>
public class AccessControl
{
    private readonly ConfigModel _cfg;
    private readonly Dictionary<string, ClientQuota> _clients = new();
    private readonly object _lock = new();

    private sealed class ClientQuota
    {
        public long Bytes;
        public long WindowStartTicks;
        public long DroppedFrames;
    }

    public AccessControl(ConfigModel cfg) => _cfg = cfg;

    public bool IsIpAllowed(string ip)
    {
        if (_cfg.BlackList.Any(pattern => IpMatch.Match(pattern, ip))) return false;
        if (_cfg.WhiteList.Count > 0 && !_cfg.WhiteList.Any(pattern => IpMatch.Match(pattern, ip))) return false;
        return true;
    }

    /// <summary>按 1 秒滑动窗口判断该客户端本帧是否可以发送。</summary>
    public bool TryConsume(string ip, long bytes) => TryConsume(ip, bytes, _cfg.ClientMaxBitRate / 8);

    /// <summary>
    /// 指定本通道的每秒字节上限。JPEG 静态帧单帧比 H.264 帧大得多，
    /// 和主链路共用一个预算会被整帧丢掉，所以按通道分别给额度。
    /// </summary>
    public bool TryConsume(string ip, long bytes, long limitPerSecond)
    {
        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var q = _clients.TryGetValue(ip, out var existing) ? existing : null;
            if (q == null)
            {
                q = new ClientQuota { Bytes = 0, WindowStartTicks = now };
                _clients[ip] = q;
            }
            else if ((now - q.WindowStartTicks) * 1000.0 / Stopwatch.Frequency >= 1000.0)
            {
                q.Bytes = 0;
                q.WindowStartTicks = now;
                q.DroppedFrames = 0;
            }

            if (q.Bytes + bytes > limitPerSecond)
            {
                q.DroppedFrames++;
                return false;
            }
            q.Bytes += bytes;
            return true;
        }
    }

    public long TakeDroppedFrames(string ip)
    {
        lock (_lock)
        {
            var q = _clients.TryGetValue(ip, out var existing) ? existing : null;
            if (q == null) return 0;
            var d = q.DroppedFrames;
            q.DroppedFrames = 0;
            return d;
        }
    }

    public void Forget(string ip)
    {
        lock (_lock) _clients.Remove(ip);
    }
}

/// <summary>IP 模式匹配：精确、尾部通配（192.168.1.*）、CIDR（192.168.1.0/24）。</summary>
public static class IpMatch
{
    public static bool Match(string pattern, string ip)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0) return false;
        if (pattern == "*") return true;

        if (pattern.Contains('/')) return MatchCidr(pattern, ip);
        if (pattern.EndsWith("*")) return ip.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, ip, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchCidr(string pattern, string ip)
    {
        var parts = pattern.Split('/');
        if (parts.Length != 2) return false;
        IPAddress network, address;
        int bits;
        try
        {
            network = IPAddress.Parse(parts[0]);
            address = IPAddress.Parse(ip);
            bits = int.Parse(parts[1]);
        }
        catch
        {
            return false;
        }

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length) return false;
        bits = Math.Clamp(bits, 0, a.Length * 8);

        var fullBytes = bits / 8;
        var remainBits = bits % 8;
        for (int i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i]) return false;
        }
        if (remainBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remainBits));
            if ((a[fullBytes] & mask) != (n[fullBytes] & mask)) return false;
        }
        return true;
    }
}
