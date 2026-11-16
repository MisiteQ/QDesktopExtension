using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QDesktopExtension;

public enum CodecType
{
    H264,
    /// <summary>保留枚举值（老配置里可能写着），实际会被 Normalize 回落成 H264。</summary>
    H265
}

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>客户端用哪种方式连进来：局域网地址 / USB 数据线 / 两种都开。</summary>
public enum LinkMode
{
    // 注意：老配置文件里没有这个字段，反序列化会拿到枚举 0 号值，
    // 所以把 Both 放在第一位，默认就是"两种方式都允许"。
    Both,
    Lan,
    Usb
}

public class ConfigModel
{
    // ---------- 网络 ----------
    public string Password { get; set; } = "123456";
    /// <summary>监听地址，"0.0.0.0" 表示局域网可访问。</summary>
    public string BindAddress { get; set; } = "0.0.0.0";
    /// <summary>WebSocket 端口：视频流 + 输入通道。</summary>
    public int WsPort { get; set; } = 8765;
    /// <summary>网页客户端 HTTP 端口：浏览器打开 http://主机:8767/ 即可。</summary>
    public int HttpPort { get; set; } = 8767;

    // ---------- 连接方式 ----------
    /// <summary>是否支持 USB 数据线直连（adb reverse）。Both = 局域网和 USB 都可用。</summary>
    public LinkMode LinkMode { get; set; } = LinkMode.Both;
    /// <summary>adb.exe 路径，留空则自动在 PATH / 常见安装位置里找。</summary>
    public string AdbPath { get; set; } = "";

    /// <summary>未通过鉴权的连接最多保留时间。</summary>
    public int AuthTimeoutMs { get; set; } = 10_000;
    /// <summary>单个客户端每秒最多接受的输入事件数，防止刷输入。</summary>
    public int MaxInputEventsPerSec { get; set; } = 300;

    // ---------- 视频 ----------
    public int Fps { get; set; } = 30;
    public long BitRate { get; set; } = 8_000_000;
    public CodecType Codec { get; set; } = CodecType.H264;
    /// <summary>关键帧间隔（秒）。新客户端靠最近的关键帧立即出画。</summary>
    public int KeyFrameIntervalSec { get; set; } = 2;
    /// <summary>编码线程数，0 = 由 FFmpeg 自动决定。</summary>
    public int EncoderThreads { get; set; } = 0;
    /// <summary>x264/x265 预设。默认 ultrafast：远程桌面更看重低延迟与低占用，画质可用码率补。</summary>
    public string EncoderPreset { get; set; } = "ultrafast";

    // ---------- JPEG 兼容通道（手机浏览器没有 WebCodecs 时用） ----------
    /// <summary>JPEG 通道帧率。走静态帧，帧率越高越跟手，但带宽线性上升。</summary>
    public int JpegFps { get; set; } = 25;
    /// <summary>JPEG 通道画面宽度（按采集宽度等比缩放）。</summary>
    public int JpegWidth { get; set; } = 960;

    // ---------- 采集 ----------
    /// <summary>采集的显示器序号（从 0 开始），-1 = 所有显示器拼成的虚拟桌面。</summary>
    public int CaptureMonitor { get; set; } = 0;
    /// <summary>
    /// 采集某个窗口而不是整屏：窗口句柄（0 = 不启用）。
    /// 句柄重启后会变，所以还要靠 <see cref="CaptureWindowTitle"/> 在启动时重新定位。
    /// </summary>
    public long CaptureWindow { get; set; }
    /// <summary>上次选中的窗口标题，用于重启后按标题找回同一个窗口。</summary>
    public string CaptureWindowTitle { get; set; } = "";

    // ---------- 虚拟扩展屏（软件合成，不需要虚拟显示器驱动） ----------
    /// <summary>虚拟画布尺寸，手机看到的就是这块屏。</summary>
    public int VirtualScreenWidth { get; set; } = 1920;
    public int VirtualScreenHeight { get; set; } = 1080;
    /// <summary>放在虚拟扩展屏上的窗口（最多 4 个，按 1/2/4 宫格排版）。</summary>
    public List<VirtualWindowCfg> VirtualWindows { get; set; } = new();
    /// <summary>当前采集源是否是虚拟扩展屏。</summary>
    public bool UseVirtualScreen { get; set; }
    /// <summary>是否把本机鼠标指针画进画面。</summary>
    public bool DrawCursor { get; set; } = true;

    // ---------- 自适应 ----------
    public bool EnableAdaptive { get; set; } = true;
    public long MinBitRate { get; set; } = 2_000_000;
    public long MaxBitRate { get; set; } = 12_000_000;
    public int MinWidth { get; set; } = 1280;
    public int MinHeight { get; set; } = 720;
    /// <summary>输出分辨率上限；0 = 跟随当前采集显示器的原生分辨率。</summary>
    public int MaxWidth { get; set; }
    /// <summary>输出分辨率上限；0 = 跟随当前采集显示器的原生分辨率。</summary>
    public int MaxHeight { get; set; }

    // ---------- 日志 ----------
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public string LogPath { get; set; } = "./logs";
    public int LogFileMaxSizeMB { get; set; } = 10;
    public int LogFileCount { get; set; } = 5;

    // ---------- 访问控制 ----------
    /// <summary>黑名单，支持尾部通配（192.168.1.*）和 CIDR（192.168.1.0/24）。</summary>
    public List<string> BlackList { get; set; } = new();
    /// <summary>白名单，非空时仅名单内 IP 可连接。</summary>
    public List<string> WhiteList { get; set; } = new();
    /// <summary>单客户端带宽上限（bit/s），超出即丢帧。</summary>
    public long ClientMaxBitRate { get; set; } = 6_000_000;

    // ---------- 输入 ----------
    /// <summary>是否允许远端注入鼠标/触摸/笔输入。</summary>
    public bool AllowRemoteInput { get; set; } = true;
    /// <summary>是否尝试注入真实笔压（InjectSyntheticPointerInput），失败自动回退为鼠标。</summary>
    public bool EnableWindowsInk { get; set; } = false;

    /// <summary>把所有数值收敛到可用范围，避免非法配置把编码器/线程搞崩。</summary>
    public void Normalize()
    {
        Fps = Math.Clamp(Fps, 1, 60);
        BitRate = Math.Clamp(BitRate, 200_000, 60_000_000);
        MaxBitRate = Math.Clamp(MaxBitRate, 100_000, 60_000_000);
        MinBitRate = Math.Clamp(MinBitRate, 100_000, MaxBitRate);
        ClientMaxBitRate = Math.Clamp(ClientMaxBitRate, 100_000, 60_000_000);
        WsPort = Math.Clamp(WsPort, 1, 65535);
        HttpPort = Math.Clamp(HttpPort, 1, 65535);
        // 密码为空等于"局域网谁都能连"：这里兜一个默认值，防止手改配置后裸奔
        if (string.IsNullOrEmpty(Password)) Password = "123456";
        // H.265 缺少 hvcC 描述符与对应封装，客户端无法解码，统一按 H.264 走
        if (Codec != CodecType.H264) Codec = CodecType.H264;
        AuthTimeoutMs = Math.Clamp(AuthTimeoutMs, 1000, 120_000);
        MaxInputEventsPerSec = Math.Clamp(MaxInputEventsPerSec, 10, 2000);
        KeyFrameIntervalSec = Math.Clamp(KeyFrameIntervalSec, 1, 30);
        EncoderThreads = Math.Clamp(EncoderThreads, 0, 32);
        if (string.IsNullOrWhiteSpace(EncoderPreset)) EncoderPreset = "ultrafast";
        JpegFps = Math.Clamp(JpegFps, 1, 30);
        JpegWidth = Math.Clamp(JpegWidth, 160, 3840);

        // yuv420p 要求偶数宽高
        MinWidth = Even(Math.Max(64, MinWidth));
        MinHeight = Even(Math.Max(64, MinHeight));
        // 上限为 0 表示跟随显示器原生分辨率，不能被下限顶回去
        if (MaxWidth > 0) MaxWidth = Even(Math.Max(MinWidth, MaxWidth));
        if (MaxHeight > 0) MaxHeight = Even(Math.Max(MinHeight, MaxHeight));

        if (string.IsNullOrWhiteSpace(LogPath)) LogPath = "./logs";
        // 相对路径按程序目录解析，而不是进程 CWD：
        // 注册表自启 / 计划任务 / 任务管理器启动时 CWD 可能是 System32，日志就会落到那里
        if (!Path.IsPathRooted(LogPath))
            LogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, LogPath));
        LogFileMaxSizeMB = Math.Clamp(LogFileMaxSizeMB, 1, 500);
        LogFileCount = Math.Clamp(LogFileCount, 1, 100);
        if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "0.0.0.0";

        // 虚拟扩展屏（软件合成，不依赖任何虚拟显示器驱动）
        VirtualScreenWidth = Even(Math.Clamp(VirtualScreenWidth, 320, 3840));
        VirtualScreenHeight = Even(Math.Clamp(VirtualScreenHeight, 180, 2160));
        VirtualWindows = (VirtualWindows ?? new List<VirtualWindowCfg>())
            .Where(w => w.Hwnd != 0)
            .GroupBy(w => w.Hwnd).Select(g => g.First())
            .Take(4).ToList();
        foreach (var w in VirtualWindows) w.Title = CleanText(w.Title);
    }

    /// <summary>
    /// 去掉字符串里的控制字符（窗口标题可能带 \r、\n 等）。
    /// 这类字符一旦原样写进 JSON 就会让整份配置变成非法 JSON，下次启动直接读不出来。
    /// </summary>
    public static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 0x20 || c == 0x7F) continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>把 JSON 文本里所有裸控制字符去掉（JSON 本身不需要换行/制表符，去掉仍是合法结构）。</summary>
    private static string StripRawControl(string json)
    {
        var sb = new StringBuilder(json.Length);
        foreach (var c in json)
        {
            if (c < 0x20 || c == 0x7F) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public bool IsLoopbackOnly() =>
        BindAddress is "127.0.0.1" or "localhost" or "::1";

    private static int Even(int v) => v % 2 == 0 ? v : v - 1;

    public static ConfigModel LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var def = new ConfigModel();
            def.Normalize();
            Save(def, path);
            return def;
        }

        var txt = File.ReadAllText(path);
        ConfigModel cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<ConfigModel>(txt, Options) ?? new ConfigModel();
        }
        catch (JsonException)
        {
            // 旧版本把窗口标题里的 \r 之类的控制字符原样写进了 JSON，导致整份配置非法。
            // 这里去掉裸控制字符再试一次；成功就顺手写回干净的文件，用户不用手工改配置。
            var cleaned = StripRawControl(txt);
            var ok = false;
            var cfg2 = new ConfigModel();
            if (cleaned != txt)
            {
                try
                {
                    cfg2 = JsonSerializer.Deserialize<ConfigModel>(cleaned, Options) ?? new ConfigModel();
                    ok = true;
                }
                catch (JsonException)
                {
                }
            }
            if (!ok)
            {
                // 彻底救不回来（比如标题里混了引号等结构性破坏）：
                // 备份坏文件 + 重建默认配置，绝不能让程序启动失败弹窗退出。
                var badPath = path + ".corrupted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(path, badPath, true); } catch { }
                cfg2.Normalize();
                try { Save(cfg2, path); } catch { }
                return cfg2;
            }

            cfg2.Normalize();
            try { Save(cfg2, path); } catch { }
            return cfg2;
        }
        cfg.Normalize();
        return cfg;
    }

    /// <summary>把配置写回磁盘（界面"保存"用），写前先收敛到合法范围。</summary>
    public static void Save(ConfigModel cfg, string path)
    {
        cfg.Normalize();
        var json = JsonSerializer.Serialize(cfg, Options);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    // 枚举必须以字符串形式读写（appconfig.json 里是 "TcpOnly"/"Info" 这种写法），
    // 默认的 System.Text.Json 只认数字，会让整份配置反序列化失败
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new SafeStringWriter());
        return options;
    }

    /// <summary>
    /// 写字符串时自己转义：控制字符一律写成 \uXXXX，其余（含中文）原样输出。
    /// 之前用 UnsafeRelaxedJsonEscaping，窗口标题里的 \r 会被原样写出，整份配置变成非法 JSON。
    /// </summary>
    private sealed class SafeStringWriter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetString() ?? "";

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == 0x7F) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            writer.WriteRawValue(sb.ToString());
        }
    }
}

/// <summary>虚拟扩展屏上的一个成员窗口（配置里存句柄 + 标题，句柄失效时按标题找回）。</summary>
public sealed class VirtualWindowCfg
{
    public long Hwnd { get; set; }
    public string Title { get; set; } = "";

    /// <summary>放到扩展屏之前的窗口位置（清掉投放时还原回去）。0 表示没记录过。</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public bool HasRect => W > 0 && H > 0;

    public override string ToString() => Title;
}
