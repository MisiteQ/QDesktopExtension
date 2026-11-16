using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QDesktopExtension;

/// <summary>
/// 极简 HTTP 服务器：只为把内置页面发给浏览器，并提供一个 /status 探针。
/// 用 TcpListener 而不是 HttpListener，避免非管理员运行时 http.sys 的 URL ACL 限制。
/// </summary>
public sealed class SimpleHttpServer : IDisposable
{
    private readonly TcpListener? _listener;
    private readonly Logger _logger;
    private readonly Thread _thread;
    private readonly int _port;
    private volatile string _indexHtml;
    private volatile bool _running;
    private Func<string>? _statusProvider;
    /// <summary>可采集窗口清单（JSON），供面板/客户端下拉使用。</summary>
    public Func<string>? WindowsProvider { get; set; }

    public SimpleHttpServer(string bindAddress, int port, Logger logger, string indexHtml)
    {
        _logger = logger;
        _indexHtml = indexHtml;
        _port = port;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "http-accept" };

        IPAddress ip;
        try
        {
            ip = BindAddress.Parse(bindAddress);
        }
        catch (Exception ex)
        {
            logger.Warn($"HTTP 绑定地址无效({bindAddress})，改用 0.0.0.0：{ex.Message}");
            ip = IPAddress.Any;
        }

        try
        {
            _listener = new TcpListener(ip, port);
        }
        catch (Exception ex)
        {
            logger.Error($"HTTP 端口 {port} 初始化失败：{ex.Message}");
            _listener = null;
            return;
        }

        try
        {
            _listener.Start(64);
            _logger.Info($"HTTP 客户端页面: http://{(ip == IPAddress.Any ? "本机IP" : ip.ToString())}:{port}/");
        }
        catch (Exception ex)
        {
            _logger.Error($"HTTP 端口 {port} 监听失败（端口被占用？）：{ex.Message}");
        }
    }

    public Func<string>? StatusProvider
    {
        get => _statusProvider;
        set => _statusProvider = value;
    }

    /// <summary>本机（回环地址）请求 /shutdown?token=... 时的回调，由 Program 触发优雅退出。</summary>
    public Action? ShutdownRequested { get; set; }

    /// <summary>停机口令，默认就是访问密码；仅回环地址可用。</summary>
    public string ShutdownToken { get; set; } = "";

    /// <summary>敏感接口（窗口清单等）要求带 ?token=访问密码，避免同网段任意设备读取。</summary>
    public string AccessToken { get; set; } = "";

    public int Port => _port;

    public void Start()
    {
        if (_listener == null) return;
        _running = true;
        _thread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener!.AcceptTcpClient();
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            using var stream = client.GetStream();

            var buffer = new byte[8192];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) return;

            var request = Encoding.ASCII.GetString(buffer, 0, read);
            var firstLine = request.Split("\r\n")[0];
            var parts = firstLine.Split(' ');
            if (parts.Length < 2) return;

            var method = parts[0];
            var target = parts[1];
            var queryStart = target.IndexOf('?');
            var path = queryStart < 0 ? target : target[..queryStart];
            var query = queryStart < 0 ? "" : target[(queryStart + 1)..];

            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                !method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                WriteResponse(stream, 405, "text/plain; charset=utf-8", "Method Not Allowed");
                return;
            }

            switch (path)
            {
                case "/":
                case "/index.html":
                    WriteResponse(stream, 200, "text/html; charset=utf-8", _indexHtml);
                    break;
                case "/status":
                {
                    // /status 会泄漏显示器布局、客户端数、编码参数等，远程必须带访问密码；
                    // 本机回环请求放行，保留 curl 排障能力
                    if (!IsLoopback(client) && !Authorized(query))
                    {
                        WriteResponse(stream, 403, "application/json; charset=utf-8",
                                      "{\"error\":\"需要 token=访问密码\"}");
                        break;
                    }
                    var json = _statusProvider?.Invoke() ?? "{}";
                    WriteResponse(stream, 200, "application/json; charset=utf-8", json);
                    break;
                }
                case "/health":
                    WriteResponse(stream, 200, "text/plain; charset=utf-8", "ok");
                    break;
                case "/windows":
                {
                    // 窗口标题属于隐私信息，必须带访问密码
                    if (!Authorized(query))
                    {
                        WriteResponse(stream, 403, "application/json; charset=utf-8",
                                      "{\"error\":\"需要 token=访问密码\"}");
                        break;
                    }
                    var json = WindowsProvider?.Invoke() ?? "{\"windows\":[]}";
                    WriteResponse(stream, 200, "application/json; charset=utf-8", json);
                    break;
                }
                case "/favicon.ico":
                    WriteResponse(stream, 204, "text/plain", "");
                    break;
                case "/caps-result":
                {
                    // 客户端排查用打点：必须带访问密码，避免被同网段任意设备刷日志
                    if (!Authorized(query))
                    {
                        WriteResponse(stream, 403, "text/plain; charset=utf-8", "forbidden");
                        break;
                    }
                    var data = QueryParam(query, "d");
                    if (data.Length > 0) _logger.Info($"客户端上报: {data[..Math.Min(data.Length, 200)]}");
                    WriteResponse(stream, 200, "text/plain; charset=utf-8", "ok");
                    break;
                }
                case "/shutdown":
                {
                    var token = QueryParam(query, "token");
                    if (!IsLoopback(client))
                    {
                        WriteResponse(stream, 403, "application/json; charset=utf-8",
                                      "{\"error\":\"shutdown 仅允许本机调用\"}");
                        break;
                    }
                    if (ShutdownToken.Length == 0 || !SlowEquals(token, ShutdownToken))
                    {
                        WriteResponse(stream, 403, "application/json; charset=utf-8",
                                      "{\"error\":\"token 不正确\"}");
                        break;
                    }
                    WriteResponse(stream, 200, "application/json; charset=utf-8", "{\"stopping\":true}");
                    _logger.Info("收到本机停机请求，准备退出");
                    Task.Run(() =>
                    {
                        Thread.Sleep(200);      // 先把响应发出去再退
                        try { ShutdownRequested?.Invoke(); } catch (Exception ex) { _logger.Error($"停机回调失败: {ex.Message}"); }
                    });
                    break;
                }
                default:
                    WriteResponse(stream, 404, "text/plain; charset=utf-8", "Not Found");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"HTTP 连接异常: {ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    /// <summary>带 token 的请求是否可信；没配访问密码时不额外限制（保持本机调试可用）。</summary>
    private bool Authorized(string query)
    {
        if (AccessToken.Length == 0) return true;
        var token = QueryParam(query, "token");
        return token.Length > 0 && SlowEquals(token, AccessToken);
    }

    private static string QueryParam(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return "";
    }

    private static bool IsLoopback(TcpClient client)
    {
        try
        {
            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            return ep != null && (ep.Address.Equals(IPAddress.Loopback) || ep.Address.Equals(IPAddress.IPv6Loopback));
        }
        catch
        {
            return false;
        }
    }

    private static bool SlowEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static void WriteResponse(Stream stream, int status, string contentType, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        stream.Write(Encoding.ASCII.GetBytes(header));
        if (bodyBytes.Length > 0) stream.Write(bodyBytes);
        stream.Flush();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        _ => "OK"
    };

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }
}

public static class BindAddress
{
    /// <summary>"0.0.0.0"/"*" → Any，"localhost" → Loopback，其余按 IP 解析。</summary>
    public static IPAddress Parse(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0 || value == "0.0.0.0" || value == "*") return IPAddress.Any;
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        return IPAddress.Parse(value);
    }
}
