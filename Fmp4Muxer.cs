using FFmpeg.AutoGen.Abstractions;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>
/// 把 H.264 码流封装成 **分片 MP4（fMP4）**，供不支持 WebCodecs、但支持 MediaSource 的浏览器
/// （手机自带浏览器常见：webcodecs=false / mse=true）用 MSE 播放，从而拿到真正的 30fps 流畅画面。
///
/// 关键点：
/// - `empty_moov + frag_keyframe + default_base_moof`：头部只写 init segment（moov），每个关键帧开始新分片；
/// - `frag_duration` 设成一帧的时长，让每个包都成为独立分片，客户端可直接按到达顺序 appendBuffer；
/// - 输出走自定义 AVIO 写回调，攒在内存里由推送层取走。
/// </summary>
public unsafe sealed class Fmp4Muxer : IDisposable
{
    private AVFormatContext* _fmt;
    private AVStream* _stream;
    private AVIOContext* _avio;
    private byte* _iobuf;
    private readonly List<byte[]> _pending = new();
    private readonly object _lock = new();
    private bool _ready;
    private bool _disposed;
    private int _fps;

    /// <summary>init segment（moov），客户端必须先 append 这段</summary>
    public byte[]? InitSegment { get; private set; }

    public bool Ready => _ready;

    public void Init(int width, int height, byte[] avcC, int fps)
    {
        Cleanup();
        if (avcC == null || avcC.Length == 0) throw new ArgumentException("avcC 为空");
        _fps = Math.Max(1, fps);

        AVFormatContext* fmt = null;
        var ret = ffmpeg.avformat_alloc_output_context2(&fmt, null, "mp4", null);
        if (ret < 0 || fmt == null) throw new InvalidOperationException($"avformat_alloc_output_context2 失败 {ret}");

        AVStream* st = null;
        AVIOContext* avio = null;
        byte* iobuf = null;
        try
        {
            st = ffmpeg.avformat_new_stream(fmt, null);
            if (st == null) throw new InvalidOperationException("avformat_new_stream 失败");

            var par = st->codecpar;
            par->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            par->codec_id = AVCodecID.AV_CODEC_ID_H264;
            par->width = width;
            par->height = height;
            par->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;

            // mov 封装要的是 avcC 形式的 extradata
            var extra = (byte*)ffmpeg.av_malloc((ulong)(avcC.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            if (extra == null) throw new InvalidOperationException("av_malloc extradata 失败");
            for (var i = 0; i < avcC.Length; i++) extra[i] = avcC[i];
            new Span<byte>(extra + avcC.Length, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();
            par->extradata = extra;
            par->extradata_size = avcC.Length;

            st->time_base = new AVRational { num = 1, den = 1000 };   // 输入时间戳按毫秒

            iobuf = (byte*)ffmpeg.av_malloc(8192);
            if (iobuf == null) throw new InvalidOperationException("av_malloc io buffer 失败");
            _selfHandle = GCHandle.Alloc(this);
            _writeCb = new avio_alloc_context_write_packet(WritePacket);
            avio = ffmpeg.avio_alloc_context(iobuf, 8192, 1, (void*)GCHandle.ToIntPtr(_selfHandle), null, _writeCb, null);
            if (avio == null) throw new InvalidOperationException("avio_alloc_context 失败");
            fmt->pb = avio;
            fmt->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            AVDictionary* opts = null;
            ffmpeg.av_dict_set(&opts, "movflags", "frag_keyframe+empty_moov+default_base_moof", 0);
            // 只在关键帧处切分片：每个分片都是独立可解码的 CMAF 片段，
            // 手机 WebView 的 MSE 对这种形态兼容性最好（按时间切的分片会落在 GOP 中间）
            ffmpeg.av_dict_set(&opts, "flush_packets", "1", 0);

            var hdr = ffmpeg.avformat_write_header(fmt, &opts);
            if (opts != null) ffmpeg.av_dict_free(&opts);
            if (hdr < 0) throw new InvalidOperationException($"avformat_write_header 失败 {hdr}");

            _fmt = fmt;
            _stream = st;
            _avio = avio;
            _iobuf = iobuf;
            _ready = true;
            // 封装器会自己挑一个 time_base，后续 pts 必须按它的刻度换算
            _inTb = new AVRational { num = 1, den = 1000 };
            _outTb = st->time_base;
            InitSegment = TakePending();          // 头部的 moov 就是 init segment
            if (InitSegment == null || InitSegment.Length == 0) throw new InvalidOperationException("未拿到 init segment");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>
    /// 写入一个 AVCC 格式的 H.264 包。
    /// timestampMs 必须是"这一帧的真实采集时刻（毫秒，单调递增）"——用墙钟而不是按帧自增，
    /// 否则实际帧率低于标称 fps 时媒体时钟会越走越慢，手机端延迟会持续累积。
    /// </summary>
    public void Write(byte[] avcc, bool keyFrame, long timestampMs)
    {
        if (!_ready || _fmt == null || _stream == null || avcc.Length == 0) return;

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        if (pkt == null) return;
        try
        {
            // FFmpeg 要求包缓冲末尾必须有 AV_INPUT_BUFFER_PADDING_SIZE 的零填充，
            // 否则它按 SIMD 批量读取时会越过缓冲区末尾（native 越界读，可能直接崩进程）
            var size = avcc.Length;
            var buf = (byte*)ffmpeg.av_malloc((ulong)(size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
            if (buf == null) return;
            for (var i = 0; i < size; i++) buf[i] = avcc[i];
            new Span<byte>(buf + size, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();

            if (ffmpeg.av_packet_from_data(pkt, buf, size) < 0)
            {
                // 失败时所有权没有转移给 packet，必须自己释放，否则每帧漏一块
                ffmpeg.av_free(buf);
                return;
            }

            if (timestampMs <= _lastMs) timestampMs = _lastMs + 1;   // 保证严格递增
            _lastMs = timestampMs;

            pkt->stream_index = _stream->index;
            pkt->pts = ffmpeg.av_rescale_q(timestampMs, _inTb, _outTb);
            pkt->dts = pkt->pts;
            if (keyFrame) pkt->flags |= ffmpeg.AV_PKT_FLAG_KEY;

            ffmpeg.av_interleaved_write_frame(_fmt, pkt);   // 内部会 unref
            // 立刻把攒在 AVIO 里的字节交给推送层，保证每个分片都能尽快出去
            ffmpeg.avio_flush(_fmt->pb);
            _ready = true;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
        }
    }

    private long _lastMs;
    private AVRational _inTb;
    private AVRational _outTb;

    /// <summary>取走写回调攒下的全部字节（一次或多次分片），没有数据时返回共享空数组（不返回 null）。</summary>
    public byte[] TakePending()
    {
        lock (_lock)
        {
            if (_pending.Count == 0) return Array.Empty<byte>();
            var total = 0;
            foreach (var b in _pending) total += b.Length;
            var outBytes = new byte[total];
            var off = 0;
            foreach (var b in _pending)
            {
                Buffer.BlockCopy(b, 0, outBytes, off, b.Length);
                off += b.Length;
            }
            _pending.Clear();
            return outBytes;
        }
    }

    private static int WritePacket(void* opaque, byte* buf, int bufSize)    {
        if (opaque == null) return bufSize;
        var muxer = GCHandle.FromIntPtr((IntPtr)opaque).Target as Fmp4Muxer;
        if (muxer == null) return bufSize;
        var chunk = new byte[bufSize];
        Marshal.Copy((IntPtr)buf, chunk, 0, bufSize);
        lock (muxer._lock) muxer._pending.Add(chunk);
        return bufSize;
    }

    private GCHandle _selfHandle;
    private avio_alloc_context_write_packet? _writeCb;

    private void Cleanup()
    {
        _ready = false;
        if (_fmt != null)
        {
            if (_fmt->pb != null && (_fmt->flags & ffmpeg.AVFMT_FLAG_CUSTOM_IO) != 0)
            {
                _fmt->pb = null;      // 我们自己释放
            }
            ffmpeg.avformat_free_context(_fmt);
            _fmt = null;
        }
        if (_avio != null)
        {
            // avio_alloc_context 的 buffer 由 avio 释放
            var avio = _avio;
            ffmpeg.avio_context_free(&avio);
            _avio = null;
            _iobuf = null;
        }
        _stream = null;
        _lastMs = 0;
        InitSegment = null;
        lock (_lock) _pending.Clear();
        if (_selfHandle.IsAllocated) _selfHandle.Free();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}
