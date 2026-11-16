# DEVELOP_DOC

## 运行结构

```
DisplayCapture (GDI/DIB 双缓冲, DPI aware)
      ↓ 独立采集线程（capture）
CaptureWorker（最新帧交接，抓一块、编码读另一块）
      ↓ BGRA + stride
StreamPipeline（编码线程：限速 → 编码 → 自适应 → 分发）
      ├─ AdaptiveController  ← 编码后帧大小
      ↓
VideoEncoder (sws_scale BGRA→YUV420P, libx264)
      ↓ EncodedFrame{bytes, keyFrame}
StreamServer
      ├─ WS 客户端（首字节 1=关键帧 / 0=增量 / 2=JPEG / 3=fMP4 分片）
      └─ SimpleHttpServer（内置 index.html + /status）
```

```
CaptureWorker._lastSlot ──→ JPEG 线程（JpegStreamEncoder，独立节拍与带宽预算）
```

采集和编码拆成两个线程的原因：GDI 抓屏单帧可能是 30ms 级（高分辨率 / 虚拟显示驱动），
串行执行时帧率上限是“采集+编码”之和；并行后只受较慢的一侧限制（实测 17fps → 22~30fps）。
JPEG 通道再单开一条线程，是因为它必须按自己的节拍取帧：和编码线程共用一轮循环时，
20fps 的出图量会把主链路压到 16fps。

## 关键约定

- **协议（文本帧，JSON）**
  - `{"type":"auth","password":"...","codec":"h264|mse|jpeg"}` →
    `{"type":"auth_ok","mode":"h264|mse|jpeg","codec":"avc1.42C028","width":...,"height":...,"fps":...,
      "description":"<base64 avcC>","input":true,
      "monitor":0,"monitors":[{"index":0,"x":0,"y":0,"w":2240,"h":1400}]}`
    `codec` 是客户端的解码能力声明：`h264`=WebCodecs、`mse`=MediaSource（fMP4）、`jpeg`=静态帧兼容通道
  - `fmp4_init`：MSE 客户端收到后先 append 这段 base64 moov 再收分片；同一客户端收到**第二次** `fmp4_init`
    表示服务端重建了封装器（新代次），客户端必须 `stopMse()` + `startMse()` 整体重来
  - `video_info`：编码器重建（分辨率变化）后推送，客户端需重置解码器并等关键帧
  - `stats`：每秒一次 `fps/kbps/clients/capturing`
  - `{"type":"input","ev":"move|down|up|wheel|pen_down|pen_move|pen_up","x":0..1,"y":0..1,"button":"left|right|middle","delta":120,"pressure":0..1}`
  - `{"type":"input","ev":"key_down"|"key_up","vk":65}`：虚拟键码（功能键/组合键）
  - `{"type":"input","ev":"text","text":"abc中"}`：Unicode 直送，不依赖键盘布局（≤256 字符）
  - `{"type":"monitor","index":0}`：切换采集显示器（`-1` = 全部显示器；影响所有观看端）
  - `{"type":"log","text":"..."}`：客户端远程日志（服务端在 `客户端日志 <ip>` 打印，截断 300 字）
- **二进制帧**（首字节为类型标记）：
  - `1` = H.264 关键帧、`0` = H.264 增量帧，其后是 AVCC 码流（4 字节长度前缀 + NAL，配合 avcC description 直接喂 WebCodecs）
  - `2` = JPEG 静态帧，其后是完整 JPEG 数据
  - `3` = fMP4 分片（MSE 客户端），`moof+mdat`，时间基 1/1000、PTS 用**墙钟毫秒**（见下）
- **JPEG 兼容通道**：`RunJpegLoop` 是独立线程，按 `JpegFps` 节拍向 `CaptureWorker.TryCopyLatest()`
  取一份像素快照（在锁内整块拷贝，避免和编码线程抢同一块缓冲出现撕裂），再 MJPEG 编码广播。
  带宽预算单独算：`max(ClientMaxBitRate/8, JpegFps × 110KB)`，否则 6Mbps 的默认上限会整帧丢弃（实测 20fps 时 0 帧）。
- **MSE（fMP4）通道**：`Fmp4Muxer` 用 FFmpeg avformat 出 `movflags=frag_keyframe+empty_moov+default_base_moof`，
  自定义 AVIO 8KB 缓冲 + 每包 `avio_flush` 才能逐包吐分片。PTS 必须用**墙钟毫秒**（输入时基 1/1000，
  `_lastMs` 强制递增）：按帧号自增会在实际帧率低于标称时让媒体时钟比墙钟慢，播放点被缓冲越推越远，
  表现就是"延迟越来越大"。封装器按 `MseGeneration` 代次重建，新客户端永远从关键帧起送。
- **能力探测**：客户端 `beacon(text)` 通过 `GET /caps-result?token=访问密码&d=` 打点，
  服务端记成 `客户端上报`；WS 不通时也能观察到连接进度。
- **线程模型**：capture 线程只抓屏并发布最新帧（`_published` 供编码消费、`_lastSlot` 供 JPEG 拷快照，
  `_readers[2]` 保证两块缓冲不会被同时读写）；管线线程编码/分发/处理切换请求；JPEG 独立线程；
  Fleck 与 HTTP 各自线程；统计用 Interlocked/Volatile 共享
- **关键帧**：`KeyFrameIntervalSec` 定时 + 新客户端接入 / 切换显示器时 `RequestKeyFrame()` 立即补一帧
- **输入**：鼠标走 `SendInput` 虚拟桌面归一化坐标；键盘可打印字符走 `KEYEVENTF_UNICODE`，
  其余走虚拟键码 + `KEYEVENTF_EXTENDEDKEY`；客户端断线/失焦时客户端与服务端都会松开残留按键
- **本机停机**：`GET /shutdown?token=<Password>`，只接受回环地址 + 定长比较口令；
  触发后取消管线 `CancellationToken`，走 `finally` 释放编码器/采集/笔，进程以 0 退出
- **发布**：`build-release.ps1` = publish + 拷 FFmpeg DLL + 拷 `install.ps1`/`uninstall.ps1` + 写 `stop.ps1`，
  publish 目录本身就是可直接安装的发布目录（单文件 exe + ffmpeg 子目录 + 三个脚本）。
  不再产出 `START.bat`/`STOP.bat`：双击 exe 就是 GUI，控制台入口用 `--console`。

## 桌面化（GUI）架构

原来的 `Program.Main` 是"建采集 → 建编码 → 起服务 → 跑管线 → Ctrl+C 停"，全程一条控制台。
要能"点图标即运行 + 面板改参数 + 关窗口正常退出"，得把这些步骤变成**可反复启停**的东西：

```
Program.RunGui()
  └─ AppHost(exe目录\appconfig.json)      ← 把"建采集/建编码/起服务/跑管线"封成后台线程
       ├─ Start(cfg) / RequestStop() / Stop(timeoutMs) / Restart(cfg)
       └─ Running / LastError / Stats / Server / ConfigPath / LocalAddresses()
     └─ TrayApp(host)                     ← ApplicationContext + NotifyIcon（托盘常驻）
          ├─ 构造时 host.Start()（双击图标即开始串流）
          └─ MainForm(host)                ← 设置/控制面板（四页参数 + 状态区 + 保存/重启）
               ├─ UiSettings(uisettings.json)   ← 只放界面偏好：CloseAction = ask|tray|exit
               └─ CloseChoiceForm()             ← 点 X 时三选一：只关面板 / 退出并停服务 / 取消
```

几个刻意的取舍：

- **单实例互斥体** `QDesktopExtension.Gui.Instance`：重复双击只弹提示让你去托盘找，不再抢 8767 端口
- **`/shutdown` 回调改为 `AppHost.RequestStop()`**，所以网页上的"关闭服务"和面板上的"停止服务"是同一条收尾路径
- **关闭流程不能在 `FormClosing` 里直接 `ShowDialog`**：窗口处于关闭状态时 WinForms 会挡掉模态子窗口，
  实测弹不出来（`ui.log` 里只看到 `closing`，没有 `ask: showing dialog`）。正确做法是
  `e.Cancel = true` + 一次性 `Timer` 延后一帧再弹，弹完按选择 `Hide()`（tray）或 `Stop(8000)` + `Close()`（exit）
- **`CloseReason.TaskManagerClosing` 也走"询问"分支**：否则用 `PostMessage(WM_CLOSE)` 或任务管理器的
  "结束任务"会被当成强制关闭直接退出，用户根本没机会选；只有 `WindowsShutDown` 才无条件直接停
- **配置分两个文件**：`appconfig.json`（服务/手机端参数，`ConfigModel.Save` 统一以 `UTF8Encoding(false)` 写）
  和 `uisettings.json`（纯界面偏好），后者不会被"保存配置"以外的逻辑改动，也不会覆盖安装时丢参数
- **开机自启** 直接读写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 值 `QDesktopExtension`，
  和 `install.ps1 -AutoStart` 写的是同一个位置，面板上的开关因此和安装参数天然一致
- **客户端提示条** `showNotice(msg, holdMs)` 带定时器：默认 6 秒后自动 `hideNotice()`，
  重复调用会清掉旧定时器（否则旧定时器会把新提示提前收掉）。JPEG 兼容提示显式传 6000ms

## 依赖版本

- `FFmpeg.AutoGen.Bindings.DynamicallyLoaded 8.1.0`（对应 FFmpeg 8.1 动态库：avcodec-62 等）
- `Fleck 1.0.2`（WebSocket 服务端）

## 扩展方向

1. 采集升级为 DXGI Desktop Duplication 或 Windows Graphics Capture（解决独占全屏 / 高帧率 / 省 CPU）
2. 剪贴板同步（注意：HTTP 非安全上下文拿不到 `navigator.clipboard`，需要 HTTPS 或 localhost）
3. WebRTC 传输（内网穿透、抖动缓冲）
4. 局域网自动发现（UDP 广播心跳 + mDNS）
5. 多客户端独立显示器视图（当前显示器切换是全局的）
6. 帧级 jitter buffer 与丢帧策略（当前是解码队列积压时丢非关键帧）

## 采集源与「扩展屏」的实现（两种形态）

`CaptureSource` 只有两类：显示器（`Monitor(i)`，-1 表示全部）和软件扩展屏（`VirtualSource()`）。
（"只显示某个窗口"这条支路暂时不进 UI，`WindowSource` 仍作为扩展屏抓取程序画面的底层实现保留。）

- **复制屏幕**：`DisplayCapture(ScreenRect)` → `BitBlt` 屏幕区域到 DIB 段，编码后推流。
- **扩展屏**：`VirtualScreen(canvasW, canvasH, windows)` 负责构图：
  - `RecomputeTiles()` 每秒核对一次成员窗口几何，按 1 / 2 / 3 / 4 个成员做铺满、左右、左整列+右上下、2×2 排版；
  - `Render()` 每帧只做一次 `BitBlt`（所有成员客户区的并集 → `_deskDc` 的 DIB 段），再对每个格子 `StretchBlt`（HALFTONE）缩放贴到画布；成员窗口最小化时才退回单窗口 `PrintWindow`；
  - `HitTest()` 把画布的归一化坐标反算成「哪个窗口 + 该窗口客户区内的桌面坐标」，落在格子之间返回 false，`TouchForwarder` 据此不注入；
  - `WindowSource.Activate()` 在按下前用 `HWND_TOPMOST → NOTOPMOST` 把目标窗口拉到 z 序最前（后台进程 `SetForegroundWindow` 常被系统拒绝，仅靠它不够）。
- 数据面：`DisplayCapture(ScreenRect, renderer)` 让虚拟屏直接作为采集器喂给 `CaptureWorker`，管线无需感知合成细节；`StreamPipeline.SwitchSource` 负责构建/销毁 `VirtualScreen` 并同步 `TouchForwarder.SetVirtualScreen()`。
- 协议：WS `{"type":"source","kind":"monitor|virtual","index":n}`；`auth_ok` 与所有 `source` 回包都带 `"virtual":<已配置程序数>`，手机端据此决定是否显示「扩展屏」选项。

> 关键取舍：**不依赖任何虚拟显示驱动**。Windows 侧真正新增一块显示器必须装 IddCx 驱动，
> 而这里改成"给手机一块由程序窗口合出来的画布"，纯用户态 GDI 完成，任何 Windows 机器都能跑。
