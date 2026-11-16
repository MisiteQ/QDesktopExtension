using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>
/// JPEG 兜底通道：给不支持 WebCodecs 的环境（手机浏览器走 http://局域网IP 属于非安全上下文，
/// VideoDecoder 不存在）提供静态帧流。每帧都是独立 JPEG，客户端用 createImageBitmap 画即可。
/// </summary>
public unsafe sealed class JpegStreamEncoder : IDisposable
{
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _pkt;
    private SwsContext* _sws;
    private int _srcW, _srcH, _outW, _outH;
    private long _pts;
    private bool _ready;
    private bool _disposed;

    public int Width => _outW;
    public int Height => _outH;

    public void Init(int srcW, int srcH, int outW, int outH)
    {
        Cleanup();
        if (srcW <= 0 || srcH <= 0 || outW <= 0 || outH <= 0) throw new ArgumentException("JPEG 尺寸非法");

        // 奇数尺寸 MJPEG 也能编，但统一成偶数便于 sws 处理
        outW = outW % 2 == 0 ? outW : outW - 1;
        outH = outH % 2 == 0 ? outH : outH - 1;

        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        if (codec == null) throw new InvalidOperationException("找不到 MJPEG 编码器（FFmpeg 动态库不完整）");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new InvalidOperationException("avcodec_alloc_context3(MJPEG) 失败");

        _ctx->width = outW;
        _ctx->height = outH;
        _ctx->time_base = new AVRational { num = 1, den = 30 };
        _ctx->framerate = new AVRational { num = 30, den = 1 };
        _ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUVJ420P;
        // 质量 2~31，越小越好；走 QSCALE 通道，MJPEG 按此换算量化
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
        _ctx->global_quality = 6 * 118;      // FF_QP2LAMBDA = 118

        var ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0) { Cleanup(); throw new InvalidOperationException($"MJPEG avcodec_open2 失败，错误码 {ret}"); }

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null) { Cleanup(); throw new InvalidOperationException("av_frame_alloc 失败"); }
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P;
        _frame->width = outW;
        _frame->height = outH;
        _frame->quality = _ctx->global_quality;
        ret = ffmpeg.av_frame_get_buffer(_frame, 32);
        if (ret < 0) { Cleanup(); throw new InvalidOperationException($"av_frame_get_buffer(MJPEG) 失败，错误码 {ret}"); }

        _pkt = ffmpeg.av_packet_alloc();
        if (_pkt == null) { Cleanup(); throw new InvalidOperationException("av_packet_alloc 失败"); }

        _sws = ffmpeg.sws_getContext(srcW, srcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                                     outW, outH, AVPixelFormat.AV_PIX_FMT_YUVJ420P,
                                     (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
        if (_sws == null) { Cleanup(); throw new InvalidOperationException("sws_getContext(JPEG) 失败"); }

        _srcW = srcW;
        _srcH = srcH;
        _outW = outW;
        _outH = outH;
        _pts = 0;
        _ready = true;
    }

    /// <summary>把一帧 BGRA 编成完整 JPEG 字节；失败返回 null。</summary>
    public byte[]? Encode(IntPtr bgraData, int srcStride)
    {
        if (!_ready || _frame == null || _ctx == null || _pkt == null) return null;
        if (ffmpeg.av_frame_make_writable(_frame) < 0) return null;

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
        _frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;   // MJPEG 每帧都是 I 帧

        if (ffmpeg.avcodec_send_frame(_ctx, _frame) < 0) return null;

        byte[]? result = null;
        while (true)
        {
            var ret = ffmpeg.avcodec_receive_packet(_ctx, _pkt);
            if (ret < 0) break;

            var data = new byte[_pkt->size];
            Marshal.Copy(new IntPtr(_pkt->data), data, 0, _pkt->size);
            ffmpeg.av_packet_unref(_pkt);
            result = data;      // MJPEG 一帧一个包
        }
        return result;
    }

    private void Cleanup()
    {
        _ready = false;
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        var pkt = _pkt; if (pkt != null) ffmpeg.av_packet_free(&pkt);
        _pkt = null;
        var frame = _frame; if (frame != null) ffmpeg.av_frame_free(&frame);
        _frame = null;
        var ctxp = _ctx; if (ctxp != null) ffmpeg.avcodec_free_context(&ctxp);
        _ctx = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup();
    }
}
