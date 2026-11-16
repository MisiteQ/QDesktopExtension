# 代码审查：QDesktopExtension / QDesktopExtension

审查时间：2026-11-14　范围：仓库全部源码（11 个文件，约 900 行）+ 构建/运行链路

结论：**提交时的代码不是"能跑但有缺陷"，而是"骨架 + 占位符"，任何一条主链路都不通。**
抓屏没有实现、编码器参数与像素格式不匹配、主循环不存在、网页客户端没有被任何代码提供、输入通道是空实现，且工程本身编译不过。

下面按严重程度列出，括号内是本次是否已修复。

## A. 阻塞级（构建 / 运行必挂）

1. **工程无法编译**（已修复）
   - `VideoEncoder` 用了 `unsafe`，`.csproj` 缺 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` → CS0227。
   - `Logger.Write` 里 `level < _cfg.LogLevel`：C# 枚举没有定义关系运算符 → CS0019。
2. **抓屏是空壳**（已实现）
   - `DisplayCapture` 只保存宽高，没有任何 `BitBlt`/DIB/WGC 调用，也没有 `Capture()` 方法。
   - `Program` 里 `screenW/screenH` 硬编码 1920×1080；本机 125%/150% DPI 下逻辑尺寸 1493×933，采集与坐标必然错位。
   - 进程未声明 DPI 感知，`GetSystemMetrics`/`BitBlt` 会拿到被系统缩放后的坐标。
3. **没有编码主循环**（已实现）
   - `Program.Main` 只有 `while(true){ Thread.Sleep(1000); 打日志 }`，从采集到编码到 `BroadcastVideo` 的调用链完全不存在，`BroadcastVideo` 从未被调用。
4. **编码器参数错误，yuv420p 编码器收到 BGRA**（已重写）
   - `_codecCtx->pix_fmt = AV_PIX_FMT_BGRA`，libx264 只接受 YUV420P/YUV422P/YUV444P 等，`avcodec_open2` 会直接失败。
   - `EncodeFrame` 把 BGRA 逐行 memcpy 到 `frame->data[0]`（那是 Y 平面），即使能开编码器，画面也是错的。
   - `receive_packet` 循环里第一帧就 `return`，后续包被丢弃且未 `av_packet_unref`。
   - 把整个关键帧当成 `_spsPpsBuffer`（SPS/PPS 应是 extradata/avcC）。
   - 只 `avcodec_close`，不 `avcodec_free_context`/`av_frame_free`，重建编码器时内存泄漏。
5. **网页客户端没有任何服务入口**（已实现）
   - `Resources/index.html` 被嵌入程序集，但代码里没有任何静态文件服务；Fleck 只做 WebSocket。
   - 页面里 `new WebSocket(ws://${location.host})` 连的是 HTTP 端口，而 WS 在 8765，连接必然失败。
6. **配置反序列化必抛异常**（已修复）
   - `appconfig.json` 里枚举写成字符串（`"TransportMode": "TcpOnly"`、`"LogLevel": "Info"`），`JsonSerializer` 默认只接受数字，会抛 `JsonException` 并让程序在启动阶段崩掉。
7. **WebCodecs 用法错误**（已重写客户端）
   - 所有帧都以 `type:"key"` 送入解码器；`timestamp` 恒为 0（要求单调递增，Chrome 会抛错）。
   - 没有传 `description`（avcC），H.264 的 AVC 码流无法被解码器配置。
   - `codec` 硬编码 `avc1.4D401F`，与实际 SPS 的 profile/level 无关。

## B. 安全与正确性

8. **鉴权形同虚设**（已修复）
   - `msg.Contains("auth") && msg.Contains(_cfg.Password)`：任何包含明文密码的消息都通过，客户端甚至不需要构造 JSON；比对还是明文、非定长。
   - 鉴权成功后没有任何后续动作（不发配置、不发关键帧、不接输入）。
   - 未鉴权连接可以永久挂着（无超时）。
9. **远端输入通道是空实现**（已实现）
   - `OnMessage` 鉴权后直接 `return`，`OnBinary` 空实现，`TouchForwarder` 永远不会被调用。
   - `mouse_event` 以相对位移方式传绝对像素：先 `SetCursorPos(x,y)` 再 `mouse_event(..., x, y)` → 光标在 x,y 基础上再移动 x,y 像素，越点越偏。
   - 没有输入限流，任何已鉴权客户端都可以无限刷事件。
10. **UDP 推流必然失败**（已实现分片）
    - 整帧直接 `UdpClient.Send`，1080p 一帧通常 30~300KB，远超 MTU，IP 层分片在 Wi-Fi/路由器上大量丢失。
    - 未 `EnableBroadcast`，`IPAddress.Broadcast` 发送可能被拒。
    - 无帧序号/分片序号，接收端无法重组。
11. **带宽配额单位与生命周期错误**（已修复）
    - `ClientMaxBitRate / 8` 是字节/秒，但配额只在主循环每秒 `ResetClientQuota()` 清空，且清空的是全部客户端；超限后每帧都打 `Quota limit` 日志刷屏，还照样继续编码。
12. **自适应控制器从未生效**（已实现）
    - `FeedFrameStat` 无调用方，`GetTarget()` 无消费者；即便被调用，改完分辨率也没有重建编码器的路径（`VideoEncoder.Reconfigure` 也没人调用）。
13. **多显示器 / 采集区域缺失**（已实现）
    - 只支持主屏，且坐标偏移恒为 0；跨屏或多屏拼合场景无法工作。

## C. 可维护性

14. **日志滚动有缺陷**（已修复）
    - 文件名按 `yyyyMMdd_HHmmss` 生成，同一秒内多次滚动会重名，导致追加进旧文件但大小计数归零 → 无限"滚动"。
    - `CleanOldLogs` 在打开新文件之前按创建时间删除，可能删掉刚打开的文件；且 `LogFileCount` 逻辑把新文件也计入。
    - `AppendAllText` 每次开关文件，30fps 下高频写日志开销大。
15. **无优雅退出**（已实现）
    - `while(true)` 无取消；Ctrl+C 直接终止进程，端口可能残留、日志无收尾。
16. **DEVELOP_DOC 与实际实现不符**（已更新）
    - 文档写 "Capture → AdaptiveController → VideoEncoder → StreamServer"，实际四者之间没有任何连线。

## D. 本次开发完成的内容

| 模块 | 变化 |
| --- | --- |
| `DisplayCapture` | GDI `BitBlt` + 自顶向下 DIB Section 采集；`EnumDisplayMonitors` 枚举显示器；`CaptureMonitor=-1` 采集虚拟桌面；可选把光标画进画面；进程 DPI 感知（PerMonitorV2） |
| `VideoEncoder` | 迁移到 FFmpeg 8.1 绑定；BGRA→YUV420P 走 `sws_scale`；设置 gop/低延迟/线程；输出 avcC extradata 供 WebCodecs 作 `description`；强制 IDR；完整释放 context |
| `StreamPipeline`（新增） | 定帧率单线程主循环：采集 → 编码 → 自适应 → 分发，落后即对齐时间避免累积延迟；每秒统计 |
| `AdaptiveController` | 30 帧窗口 + 2 秒冷却 + 迟滞；只按码率档位选分辨率，保持宽高比与偶数尺寸 |
| `StreamServer` | 新客户端立即补发最近关键帧；`auth_ok` 带 codec/description/分辨率；`video_info` 通知编码器重建；每秒 `stats`；未鉴权 10s 超时；定长密码比较；输入事件每秒限流 |
| `SimpleHttpServer`（新增） | `TcpListener` 实现的静态页服务（避开 http.sys ACL），提供 `/`、`/status`、`/health` |
| `TouchForwarder` / `InkPen` | 鼠标改用 `SendInput` + 虚拟桌面绝对坐标（多屏正确、不再二次偏移）；笔压走 `CreateSyntheticPointerDevice` + `InjectSyntheticPointerInput`，失败回退鼠标 |
| `UdpStreamer` | 1400 字节分片 + 8 字节头（帧序号/分片序号/总数/标志），开启广播 |
| `AccessControl` | 每秒窗口配额；IP 支持 `192.168.1.*` 与 CIDR |
| `index.html` | 真正的 WebCodecs 客户端：avcC description、关键帧标记（首字节）、时间戳单调、积压丢帧、HUD 状态、密码登录、断线重连、pointer/wheel/pen 输入 |
| `Logger` | 枚举比较修正；按大小滚动（带序号防重名）；按时间保留 N 个 |
| 配置/文档 | 新增 HttpPort/CaptureMonitor/DrawCursor/AllowRemoteInput 等；README、DEVELOP_DOC 重写 |

## E. 验证结果

见 `VERIFY.md`（构建 + 实机运行记录）。
