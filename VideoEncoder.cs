using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

public readonly record struct EncodedFrame(byte[] Data, bool KeyFrame);

/// <summary>
/// FFmpeg (libx264 / libx265) 编码封装。
/// 采集得到的是 BGRA，编码器吃 YUV420P，中间必须经过 sws_scale 转换。
/// FFmpeg 输出的 extradata/码流是 Annex B，这里转换成 WebCodecs 需要的
/// avcC description + AVCC（4 字节长度前缀）码流。
/// </summary>
public unsafe sealed class VideoEncoder : IDisposable
{
    private readonly ConfigModel _cfg;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;

    private int _encW, _encH;
    private int _srcW, _srcH;
    private int _fps;
    private long _bitrate;
    private long _pts;
    private bool _ready;
    private byte[]? _extradata;   // FFmpeg 原始 extradata（Annex B 的 SPS/PPS）
    private byte[]? _avcC;        // 转换后的 avcC，给 WebCodecs 当 description
    private string? _codecString;
    // 编码输出中间缓冲复用：避免每帧 new byte[_pkt->size]。
    // 消费（BroadcastFrame/FeedMuxer）都在编码线程同步完成，下一帧覆盖前已消费完，安全。
    private byte[]? _rawBuf;

    private static byte[] EnsureBuffer(ref byte[]? buf, int len)
    {
        if (buf == null || buf.Length < len) buf = new byte[len];
        return buf;
    }

    public VideoEncoder(ConfigModel cfg)
    {
        _cfg = cfg;
        // FFmpeg 动态库放在程序目录的 ffmpeg 子目录，首次使用前定位一次
        DynamicallyLoadedBindings.LibrariesPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        DynamicallyLoadedBindings.Initialize();
    }

    public bool Ready => _ready;
    public int Width => _encW;
    public int Height => _encH;
    public long BitRate => _bitrate;

    /// <summary>WebCodecs 的 description（avcC）。H265 暂不提供。</summary>
    public byte[]? Extradata => _avcC;

    /// <summary>FFmpeg 原始 extradata（Annex B 的 SPS/PPS），自检落盘时用来补头。</summary>
    public byte[]? RawExtradata => _extradata;

    /// <summary>WebCodecs 需要的 codec 字符串，Profile/Level 从 SPS 推导。</summary>
    public string CodecString() =>
        _codecString ?? (_cfg.Codec == CodecType.H265 ? "hvc1.1.6.L93.B0" : "avc1.42001E");

    public void Init(int encW, int encH, int srcW, int srcH, int fps, long bitrate)
    {
        Cleanup();
        if (encW <= 0 || encH <= 0) throw new ArgumentException("编码尺寸非法");

        var codecId = _cfg.Codec == CodecType.H265 ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        var codec = ffmpeg.avcodec_find_encoder(codecId);
        if (codec == null)
        {
            throw new InvalidOperationException(
                $"找不到 {codecId} 编码器。请确认 ffmpeg 目录下已放入带 libx264/libx265 的 FFmpeg 动态库（avcodec-59.dll 等）。");
        }

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("avcodec_alloc_context3 失败");

        _ctx->width = encW;
        _ctx->height = encH;
        _ctx->time_base = new AVRational { num = 1, den = fps };
        _ctx->framerate = new AVRational { num = fps, den = 1 };
        _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _ctx->bit_rate = bitrate;
        _ctx->rc_max_rate = bitrate;
        _ctx->rc_buffer_size = (int)Math.Min(int.MaxValue, bitrate * 2);
        _ctx->gop_size = Math.Max(1, fps * Math.Max(1, _cfg.KeyFrameIntervalSec));
        _ctx->max_b_frames = 0;              // 低延迟，且关键帧判断简单
        _ctx->thread_count = _cfg.EncoderThreads;
        // 让编码器把 SPS/PPS 放进 extradata（avcC），客户端才能用 description 配置 WebCodecs
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (_cfg.Codec == CodecType.H264)
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", _cfg.EncoderPreset, 0);
            ffmpeg.av_opt_set(_ctx->priv_data, "tune", "zerolatency", 0);
            ffmpeg.av_opt_set(_ctx->priv_data, "profile", "main", 0);
        }
        else
        {
            ffmpeg.av_opt_set(_ctx->priv_data, "preset", _cfg.EncoderPreset, 0);
        }

        var ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0)
        {
            Cleanup();
            throw new InvalidOperationException($"avcodec_open2 失败，错误码 {ret}");
        }

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null) { Cleanup(); throw new InvalidOperationException("av_frame_alloc 失败"); }
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
        _frame->width = encW;
        _frame->height = encH;
        ret = ffmpeg.av_frame_get_buffer(_frame, 32);
        if (ret < 0) { Cleanup(); throw new InvalidOperationException($"av_frame_get_buffer 失败，错误码 {ret}"); }

        _pkt = ffmpeg.av_packet_alloc();
        if (_pkt == null) { Cleanup(); throw new InvalidOperationException("av_packet_alloc 失败"); }

        _encW = encW;
        _encH = encH;
        _srcW = srcW;
        _srcH = srcH;
        _fps = fps;
        _bitrate = bitrate;
        _pts = 0;
        RebuildScaler();

        _extradata = CopyExtradata();
        BuildAvcC(_extradata);
        _ready = true;
    }

    /// <summary>
    /// 从 FFmpeg 的 Annex B extradata 里取 SPS/PPS，拼出 MP4/WebCodecs 使用的 avcC。
    /// 同时从 SPS 推导 codec 字符串（profile / constraint / level）。
    /// </summary>
    private void BuildAvcC(byte[]? extradata)
    {
        _avcC = null;
        _codecString = null;
        if (_cfg.Codec != CodecType.H264 || extradata == null) return;

        byte[]? sps = null, pps = null;
        foreach (var (offset, length) in SplitNals(extradata))
        {
            var header = extradata[offset];
            var nalType = header & 0x1F;
            var payload = new byte[length];
            Buffer.BlockCopy(extradata, offset, payload, 0, length);
            if (nalType == 7 && sps == null) sps = payload;
            else if (nalType == 8 && pps == null) pps = payload;
        }
        if (sps == null || pps == null || sps.Length < 4) return;

        _codecString = $"avc1.{sps[1]:X2}{sps[2]:X2}{sps[3]:X2}";

        var avcC = new byte[7 + 2 + sps.Length + 1 + 2 + pps.Length];
        var i = 0;
        avcC[i++] = 1;                  // configurationVersion
        avcC[i++] = sps[1];             // AVCProfileIndication
        avcC[i++] = sps[2];             // profile_compatibility
        avcC[i++] = sps[3];             // AVCLevelIndication
        avcC[i++] = 0xFF;               // 6 bits reserved + lengthSizeMinusOne=3
        avcC[i++] = 0xE1;               // 3 bits reserved + numOfSequenceParameterSets=1
        avcC[i++] = (byte)(sps.Length >> 8);
        avcC[i++] = (byte)(sps.Length & 0xFF);
        Buffer.BlockCopy(sps, 0, avcC, i, sps.Length);
        i += sps.Length;
        avcC[i++] = 1;                  // numOfPictureParameterSets
        avcC[i++] = (byte)(pps.Length >> 8);
        avcC[i++] = (byte)(pps.Length & 0xFF);
        Buffer.BlockCopy(pps, 0, avcC, i, pps.Length);

        _avcC = avcC;
    }

    /// <summary>按起始码切分 Annex B 码流，返回每个 NAL 的数据偏移与长度（不含起始码）。</summary>
    private static List<(int Offset, int Length)> SplitNals(byte[] data)
    {
        var result = new List<(int, int)>();
        var i = 0;
        while (i + 3 <= data.Length)
        {
            if (data[i] != 0 || data[i + 1] != 0) { i++; continue; }
            var scLen = data[i + 2] == 1 ? 3 : (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0);
            if (scLen == 0) { i++; continue; }
            var start = i + scLen;
            var next = start;
            var end = data.Length;
            while (next + 3 <= data.Length)
            {
                if (data[next] == 0 && data[next + 1] == 0 &&
                    (data[next + 2] == 1 || (next + 4 <= data.Length && data[next + 2] == 0 && data[next + 3] == 1)))
                {
                    end = next;
                    break;
                }
                next++;
            }
            if (end > start) result.Add((start, end - start));
            i = end;
            if (end == data.Length) break;
        }
        return result;
    }

    /// <summary>Annex B（起始码）转 AVCC（4 字节长度前缀）。</summary>
    private static byte[] ToAvcc(byte[] annexb)
    {
        var nals = SplitNals(annexb);
        var total = 0;
        foreach (var (_, length) in nals) total += 4 + length;

        var result = new byte[total];
        var w = 0;
        foreach (var (offset, length) in nals)
        {
            result[w++] = (byte)(length >> 24);
            result[w++] = (byte)(length >> 16);
            result[w++] = (byte)(length >> 8);
            result[w++] = (byte)length;
            Buffer.BlockCopy(annexb, offset, result, w, length);
            w += length;
        }
        return result;
    }

    private void RebuildScaler()
    {
        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }
        _sws = ffmpeg.sws_getContext(
            _srcW, _srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
            _encW, _encH, AVPixelFormat.AV_PIX_FMT_YUV420P,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getContext 失败（尺寸非法）");
    }

    private byte[]? CopyExtradata()
    {
        if (_ctx->extradata == null || _ctx->extradata_size <= 0) return null;
        var buf = new byte[_ctx->extradata_size];
        Marshal.Copy((IntPtr)_ctx->extradata, buf, 0, buf.Length);
        return buf;
    }

    /// <summary>
    /// 编码一帧。返回 0~n 个输出包（B 帧关闭时通常为 1 个）。
    /// 编码器重置过（分辨率变化）时 extradata 会更新，调用方需要同步给客户端。
    /// </summary>
    public List<EncodedFrame> Encode(IntPtr bgraData, int srcStride, bool forceKeyFrame)
    {
        var output = new List<EncodedFrame>(1);
        if (!_ready || _frame == null || _ctx == null) return output;

        var ret = ffmpeg.av_frame_make_writable(_frame);
        if (ret < 0) return output;

        var srcData = new byte*[4];
        srcData[0] = (byte*)bgraData;
        var srcStrides = new int[4];
        srcStrides[0] = srcStride;

        var dstData = new byte*[4];
        dstData[0] = _frame->data[0];
        dstData[1] = _frame->data[1];
        dstData[2] = _frame->data[2];

        var dstStrides = new int[4];
        dstStrides[0] = _frame->linesize[0];
        dstStrides[1] = _frame->linesize[1];
        dstStrides[2] = _frame->linesize[2];

        ffmpeg.sws_scale(_sws, srcData, srcStrides, 0, _srcH, dstData, dstStrides);

        _frame->pts = _pts++;
        _frame->pict_type = forceKeyFrame ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;

        ret = ffmpeg.avcodec_send_frame(_ctx, _frame);
        if (ret < 0) return output;

        while (true)
        {
            var r = ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF) break;
            if (r < 0) break;
            var key = (_pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
            // 复用中间缓冲，避免每帧分配
            var raw = EnsureBuffer(ref _rawBuf, _pkt->size);
            Marshal.Copy((IntPtr)_pkt->data, raw, 0, raw.Length);
            // WebCodecs 配了 avcC description 时要求 AVCC 长度前缀格式
            var bytes = _cfg.Codec == CodecType.H264 ? ToAvcc(raw) : raw;
            output.Add(new EncodedFrame(bytes, key));
            ffmpeg.av_packet_unref(_pkt);
        }
        return output;
    }

    /// <summary>采集分辨率变化（例如换了显示器）时重建缩放器。</summary>
    public void UpdateSourceSize(int srcW, int srcH)
    {
        if (srcW == _srcW && srcH == _srcH) return;
        _srcW = srcW;
        _srcH = srcH;
        if (_ready) RebuildScaler();
    }

    public void Cleanup()
    {
        _ready = false;
        try
        {
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_pkt != null) { fixed (AVPacket** p = &_pkt) ffmpeg.av_packet_free(p); _pkt = null; }
            if (_frame != null) { fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p); _frame = null; }
            if (_ctx != null) { fixed (AVCodecContext** p = &_ctx) ffmpeg.avcodec_free_context(p); _ctx = null; }
        }
        catch
        {
        }
    }

    public void Dispose() => Cleanup();
}
