using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace QDesktopExtension;

/// <summary>
/// 自检：不依赖屏幕内容，生成合成图案 → 编码 → 落盘为 Annex B 码流，
/// 用于确认 FFmpeg 依赖、BGRA→YUV420P 转换、关键帧与码流结构是否正常。
/// 生成的 .h264 可以直接用 ffmpeg 解码成图片核对颜色。
/// </summary>
internal static class SelfTest
{
    public static unsafe int Run(ConfigModel cfg, Logger logger, string[] args)
    {
        var outPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "selftest.h264");
        const int width = 1280;
        const int height = 720;
        const int fps = 30;
        const int frameCount = 90;

        var encoder = new VideoEncoder(cfg);
        try
        {
            encoder.Init(width, height, width, height, fps, 8_000_000);
        }
        catch (Exception ex)
        {
            logger.Error($"自检失败，编码器初始化异常: {ex.Message}");
            return 3;
        }

        logger.Info($"编码器就绪: codec={encoder.CodecString()} extradata={encoder.Extradata?.Length ?? 0}B");
        if (encoder.Extradata is { Length: > 0 })
        {
            logger.Info($"extradata(base64)={Convert.ToBase64String(encoder.Extradata)}");
        }

        var pixels = BuildBaseImage(width, height);
        var stride = width * 4;
        var keyFrames = 0;
        var packetCount = 0;
        long totalBytes = 0;

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        // 因为编码器开启了 GLOBAL_HEADER，码流里不再重复 SPS/PPS，
        // 落盘成裸 .h264 时要手动补上，ffmpeg 才能独立解码。
        if (encoder.RawExtradata is { Length: > 0 } rawHeaders) fs.Write(rawHeaders);
        fixed (byte* basePtr = pixels)
        {
            for (var i = 0; i < frameCount; i++)
            {
                DrawMovingBlock(pixels, width, height, i);
                var forceKey = i == 0 || i % fps == 0;
                var packets = encoder.Encode((IntPtr)basePtr, stride, forceKey);
                foreach (var packet in packets)
                {
                    packetCount++;
                    totalBytes += packet.Data.Length;
                    if (packet.KeyFrame) keyFrames++;
                    WriteAnnexB(fs, packet.Data);
                }
            }
        }

        encoder.Dispose();
        logger.Info($"自检完成: {frameCount} 帧, 输出包 {packetCount} 个, 关键帧 {keyFrames} 个, {totalBytes / 1024} KB");
        logger.Info($"码流已写入: {outPath}");
        return 0;
    }

    /// <summary>生成一张横向蓝→绿渐变 + 顶部色条的基准图（BGRA）。</summary>
    private static byte[] BuildBaseImage(int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                buf[i + 0] = (byte)(255 - (x * 255 / w));          // B
                buf[i + 1] = (byte)(y * 255 / h);                  // G
                buf[i + 2] = (byte)(x * 255 / w);                  // R
                buf[i + 3] = 255;
            }
        }
        // 顶部 40px 纯白条，便于肉眼确认方向与缩放
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                buf[i + 0] = 255;
                buf[i + 1] = 255;
                buf[i + 2] = 255;
            }
        }
        return buf;
    }

    /// <summary>画一个移动的方块，制造帧间差异（验证 P 帧与运动补偿）。</summary>
    private static void DrawMovingBlock(byte[] buf, int w, int h, int frame)
    {
        const int size = 160;
        var cx = 80 + (frame * 8) % (w - size - 160);
        var cy = 200 + (int)(Math.Sin(frame / 10.0) * 120) + 120;
        for (var y = cy; y < Math.Min(cy + size, h); y++)
        {
            for (var x = cx; x < Math.Min(cx + size, w); x++)
            {
                var i = (y * w + x) * 4;
                buf[i + 0] = 40;     // B
                buf[i + 1] = 40;     // G
                buf[i + 2] = 230;    // R
            }
        }
    }

    /// <summary>AVCC（4 字节长度前缀）转 Annex B（00 00 00 01 起始码），方便 ffmpeg 直接解码核对。</summary>
    private static void WriteAnnexB(FileStream fs, byte[] packet)
    {
        var pos = 0;
        while (pos + 4 <= packet.Length)
        {
            var n = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(pos, 4));
            if (n <= 0 || pos + 4 + n > packet.Length) break;
            fs.Write(new byte[] { 0, 0, 0, 1 });
            fs.Write(packet, pos + 4, n);
            pos += 4 + n;
        }
    }
}
