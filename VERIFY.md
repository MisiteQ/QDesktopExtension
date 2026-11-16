# 验证记录（2026-11-14）

环境：Windows 10/11，.NET SDK 8.0.428（用户目录临时安装），FFmpeg 8.1.3 gpl-shared（BtbN 构建），
显示器 1 台（物理 2240×1400@(0,0)，150% 缩放），显卡 Intel Iris Xe + 两个虚拟显示驱动
（AskLinkIddDriver / RayLink Virtual Display Adapter）。

说明：Agent 的会话大部分时间是非交互桌面会话（`CopyFromScreen` 曾持续报"句柄无效"），
因此第一轮验证靠「合成图案 → 编码 → 落盘 → ffmpeg 解码回读」；本轮该会话已能真实抓屏，
补上了真实桌面的端到端验证（见第 6 节）。

## 1. 构建

```
dotnet build            → 已成功生成，0 警告 0 错误
dotnet publish -c Release → 单文件 QDesktopExtension.exe（93.7 MB，self-contained）
```

## 2. 编码链路自检（`--selftest`）

```
QDesktopExtension.exe --selftest selftest.h264
编码器就绪: codec=avc1.4D4020 extradata=38B
自检完成: 90 帧, 输出包 90 个, 关键帧 3 个, 464 KB
```

- `avc1.4D4020`：从 SPS 解析出 Main Profile / Level 3.2 → SPS 解析与 avcC 构造正确
- `extradata=38B`：avcC（1 + profile + compat + level + 0xFF + 0xE1 + SPS + PPS）
- 用 ffmpeg 回读：顶部白条在上、蓝→红渐变、绿色分量随 y 递增、红色方块位置正确
  → BGRA→YUV420P 通道顺序与上下方向均正确，不是黑帧

## 3. 运行与协议（Debug / Release 单文件各验一次）

```
GET http://127.0.0.1:8767/status
{"uptime":4,"fps":30,"kbps":89,"avgKbps":96,"frames":131,"clients":0,"codec":"avc1.4D4028",
 "width":1920,"height":1080,"monitor":0,"monitors":[{"index":0,"x":0,"y":0,"w":2240,"h":1400}],
 "inputEvents":0,"keysTyped":0,"adaptive":true,"input":true}
```

WebSocket 实测：

```
发送 {"type":"auth","password":"123456"}
[0] Text  {"type":"auth_ok","codec":"avc1.42C028","width":1920,"height":1080,"fps":30,
           "description":"<base64 avcC>","monitor":0,"monitors":[...],
           "input":true}
[1] Binary 首字节=1（关键帧），其后 AVCC 长度前缀 + NAL
[2] Binary 首字节=0（增量帧）
```

鉴权 → 配置下发 → 新客户端立即收到缓存关键帧 → 后续增量帧，与 WebCodecs 的要求一致。

## 4. 自适应行为

静止画面下连续 12 秒只出现一次重建（加了"利用率下限"后不再反复重建），帧率稳定、码率 ~80–90 kbps。

## 5. 本轮新增功能验证

### 5.1 键盘输入（协议层）

```
发送 {"type":"input","ev":"text","text":"键盘测试"}     → status inputEvents+1, keysTyped+4
发送 {"type":"input","ev":"key_down","vk":13}          → inputEvents+1, keysTyped+1
发送 {"type":"input","ev":"key_up","vk":13}            → inputEvents+1, keysTyped+1
```

`/status` 结果 `"inputEvents":3,"keysTyped":6`（4 个汉字 + 回车按下/抬起各计 1），
说明文本模式与虚拟键模式都走到了注入路径。断线时服务端会按 `ClientState.PressedKeys`
逐个松开，且 Fleck 重复回调 `OnClose` 时只清理一次（实测日志只出现 1 条"客户端断开"）。

### 5.2 显示器切换

```
发送 {"type":"monitor","index":0}
日志：已切换显示器: 请求=0 生效=0 区域=2240x1400@(0,0) 编码=1920x1080
     编码参数同步: avc1.42C028 1920x1080 desc=...
```

越界序号（如只有 1 台显示器时请求 `index:1`）会落到有效序号 0，`/status` 里 `monitor` 也随之变成 0，
不会留下一个不存在的屏幕号。

## 6. 真实桌面端到端验证（本轮新增）

做法：用 `ClientWebSocket` 抓取 20 个 WS 二进制包，按 `auth_ok` 里的 avcC 拆出 SPS/PPS，
把每个包的 AVCC 长度前缀替换成 Annex B 起始码，交 ffmpeg 解码成 PNG。

```
packets=20  annexb=1009822 bytes
ffmpeg -i live.h264 -frames:v 1 -vf scale=1120:-1 live.png   → 407 KB PNG
```

解码出的画面：浏览器窗口、Bilibili 页面、视频播放器、鼠标指针、中文文字全部清晰可读，
颜色正常、上下方向正确。也就是说「真实抓屏 → BGRA→YUV420P → libx264 → AVCC → 客户端」
整条链路在真实桌面上跑通，不再只是合成图案自检。

## 7. 性能实测与相应改动

| 观测 | 数值 |
| --- | --- |
| 单次 `BitBlt`（2240×1400，独立基准） | ~33 ms |
| 单次 `BitBlt`（1920×1080） | ~17 ms |
| 单次 `BitBlt`（1280×720） | ~16 ms（≈ 固定 16.7ms，说明被 DWM/vsync 量化） |
| `CAPTUREBLT` 加入后 | 更慢且掉帧，已去掉，只用 SRCCOPY |
| 串行（采集+编码同线程） | 17–19 fps（采集 40ms + 编码 11–26ms） |
| 采集/编码并行后 | 22–30 fps（编码换 `ultrafast` 后 11–20ms） |
| 总 CPU（16 逻辑核） | ~30%，其中本进程约 4 核 |

对应改动：采集线程独立 + 双缓冲最新帧交接（`CaptureWorker`）、`timeBeginPeriod(1)` 提升
`Thread.Sleep` 精度、去掉 `CAPTUREBLT`、编码预设可配置且默认 `ultrafast`。
结论：本机帧率上限主要由 GDI 抓屏的 33ms/帧决定（两个虚拟显示驱动放大了读回成本），
普通显卡驱动环境下 GDI 抓屏通常 5–10ms，可跑满 30fps；要在这类机器上稳定 30fps，
下一步应换 DXGI Desktop Duplication / WGC（已写入扩展方向）。

## 8. 正式启动/运行验收（本轮）

打包：`build-release.ps1` → `publish\` 目录含
`QDesktopExtension.exe`(93.8MB) + `appconfig.json` + `ffmpeg\` + `START.bat` + `STOP.bat`/`stop.ps1` + `logs\`。

| 验收项 | 结果 |
| --- | --- |
| `START.bat` 启动（`cmd /c`，隐藏窗口） | 6 秒后 `/health` 返回 `ok`；`/status` 显示 `fps=31`、`monitor=0`、`monitors=1` |
| `STOP.bat` / `stop.ps1` 优雅停机 | 返回"已发送停机请求"；进程消失、8767 端口关闭；日志依次出现"收到本机停机请求，准备退出"与"已退出" |
| 进程退出码 | `0`（正常停机） |
| 错误口令 `/shutdown?token=wrong` | `403 {"error":"token 不正确"}` |
| 重复启动第二个实例 | 退出码 `4`，日志给出 `HTTP 端口 8767 监听失败（端口被占用？）` + `服务启动失败` |
| `--help` | 正常输出用法（配置/selftest/help/退出方式） |
| `--selftest` | 仍正常：90 帧、3 个关键帧、767KB，码流可解码 |
| 日志目录 | `publish\logs\` 自动创建并写入 |
| 锁屏态（抓屏失败） | `/status` 返回 `capturing:false, fps:0, frames:0`；不推陈旧帧；警告日志限流为 ~3 秒一条；日志行标注"（抓屏不可用）"；恢复后自动继续 |

打包过程中发现并修掉的两个真实问题：

1. 之前手工拷 DLL 的方式容易漏，改成 `build-release.ps1` 统一打包，FFmpeg 库缺失时会 `Warning` 提示；
2. `.bat` 里写中文提示 + `chcp 65001` 在 UTF-8/BOM 下会被 cmd 解析坏（实测停机脚本直接报语法错、服务没停），
   改为 **`.bat` 只放 ASCII**、中文提示统一放 `stop.ps1`（UTF-8 带 BOM，兼容 Windows PowerShell 5.1 与 pwsh）。

## 9. 手机黑屏排查（本轮）

现场：服务端 `capturing:true`、fps 20–30、frames 持续增长、`clients:1`（手机 IP 192.168.31.75 已鉴权，模式 H.264），
说明**服务端在正常出帧**，黑屏发生在客户端侧。

根因与修复：

| 环节 | 事实 / 处理 |
| --- | --- |
| WebCodecs 只在安全上下文可用 | 手机走 `http://192.168.x.x` 时 `VideoDecoder` 为 undefined；页面此前把错误写在隐藏的登录层里，用户只看到黑 canvas |
| 提示不可见 | 新增独立 `#notice` 顶部浮层（固定在画面之上，与登录层无关），错误与兼容模式都显示在这里 |
| 无降级方案 | 新增 **JPEG 兼容通道**：`auth` 带 `codec:"jpeg"` → `auth_ok.mode:"jpeg"`；管线按 ~5fps 用 MJPEG 编出 1280 宽 JPEG，二进制帧首字节 `2` 标记，客户端 `createImageBitmap` 直接画 |
| H.264 解码失败无自救 | 客户端 4 秒内没有画出一帧、或解码器 `error` / `configure` 抛错，自动重连并申请 JPEG（只降级一次，避免抖动） |
| 只有 JPEG 客户端时才付代价 | 无 JPEG 客户端时完全不创建 JPEG 编码器，主链路开销为零 |

验证（本机真实客户端，不是模拟）：

| 项目 | 结果 |
| --- | --- |
| 手机机型 / 系统 | 小米 21091116C，Android 13，自带 `com.android.browser`（MIUI WebView），**已通过 USB + adb 实测** |
| 手机浏览器能力 | 该浏览器没有 WebCodecs（`auth` 后服务端日志 `模式: JPEG`），所以 JPEG 兼容通道是手机的主路径 |
| JPEG 客户端鉴权 | `auth_ok.mode:"jpeg"`；日志"鉴权通过（模式: JPEG）"+"JPEG 兼容通道就绪: 1024x640" |
| JPEG 帧内容 | 每帧 ~83KB / 1024×640 / ~6fps；落盘为 jpg 用图像查看**画面完整正确**（浏览器 + Bilibili 页面，文字清晰、方向正确） |
| 手机端真实显示 | `adb exec-out screencap` 与 `uiautomator dump` 双重确认：WebView 里 `canvas id=cv` 正在渲染**本机桌面**（含 Windows 任务栏），顶部橙色提示条可见 → 手机不再黑屏 |
| H.264 客户端不受影响 | 同一时刻另一客户端收到 1 关键帧 + 29 增量帧，`/status` fps=30 |
| JPEG 帧率上限（实测） | 6fps 时主链路保持 23–30fps；8fps 会把主链路压到 16fps，因此定在 6fps |
| 抓不到屏时不推垃圾帧 | `capturing:false` 时 fps=0、frames 不增长，日志标注"（抓屏不可用）" |
| USB 端口转发 | `adb reverse tcp:8767/8765` 已建立；但小米自带浏览器对 `http://localhost:8767` 报"网页打开失败"，故手机实际用局域网 IP 访问 |

## 10. 手机端流畅度专项（本轮，adb 实测）

用户现场反馈"手机连上后延迟越来越严重、肉眼可见卡顿"。逐段定位到三处真实缺陷，全部修完再实测：

| 环节 | 事实 | 处理 |
| --- | --- | --- |
| fMP4 媒体时钟漂移 | `Fmp4Muxer.Write` 原按帧号自增 PTS，输入时基 1/fps；实际帧率低于 30 时媒体时间比墙钟慢（实测 11s 墙钟只走 8s），播放点被缓冲越推越远 | 改为**墙钟毫秒** PTS（输入时基 1/1000，`_lastMs` 强制递增，管线用 `Stopwatch` 算真实毫秒） |
| 新客户端从 GOP 中间接管 | 用"客户端数量"判断是否重建封装器：老客户端断开后数量回 1，新客户端进来仍是 1 → 复用旧封装器，永远等不到关键帧 | 引入 `MseGeneration` 代次，鉴权即 `++`；管线见代次变化就 dispose 重建，且必须 `RequestKeyFrame()` + 跳过非关键帧 |
| JPEG 通道抢不到帧 | 管线用 `PeekLatest` 抢最新帧，而编码线程几乎立刻消费掉同一帧 → 6 秒只出 1 帧 | `CaptureWorker` 增 `_lastSlot` + `TryCopyLatest(dst,stride,h)`，在锁内整块 `MemoryCopy` 出快照；JPEG 线程持有自己的像素缓冲（`Marshal.AllocHGlobal`） |
| JPEG 被带宽限流整帧丢弃 | `AccessControl.TryConsume` 用 `ClientMaxBitRate/8`=750KB/s，而 20fps JPEG ≈1.4MB/s → 全部丢帧（服务端日志"JPEG 通道诊断: 客户端 1, 编码器 就绪"但客户端 0 帧） | `TryConsume(ip,bytes,limitPerSecond)` 重载；JPEG 通道预算 `max(ClientMaxBitRate/8, JpegFps×110KB)` |

实测结果（本机 + 真机，服务端与手机同时跑）：

| 项目 | 结果 |
| --- | --- |
| JPEG 通道吞吐（服务端探针，6 秒） | 20fps 配置：**121 帧**；25fps 配置：**150 帧**（帧间隔稳定，无整帧丢弃） |
| 手机端真实渲染帧率（客户端 `clog` 回传） | 20fps 配置：**19/20/20fps**；25fps 配置：**24/25/30/27fps**（`decFps` 每秒采样） |
| 拷像素不再占抓屏锁 | `TryCopyLatest` 改成"锁内占槽 → 出锁拷贝 → 回锁释放"，避免 12MB `memcpy` 挡住抓屏线程；改前带 JPEG 客户端主链路掉到 18–23fps，改后回到 26–27fps |
| 手机端端到端延迟（HUD） | `延迟 ~11ms`–`~71ms`（画面 HUD 可见"已连接 1920x1080 28fps 4254kbps 解码 41fps 延迟 ~11ms 客户端 1"） |
| 主链路不受拖累 | 同一时刻 `/status` fps=26–31、采集 35–45ms、编码 18ms；JPEG 客户端与 H.264 客户端互不影响 |
| 输入注入（真机） | 手机点画面中心 → 本机物理光标落在 **(1120,700)**，即 2240×1400 的中点，映射正确；`adb` 的 `input tap` 坐标是**横屏空间**（2400×1080），按竖屏坐标点会打空，这是之前"点了没反应"的排查误区 |
| 画面正确性 | `adb exec-out screencap` 抓手机屏：桌面镜像完整、文字清晰、比例正确，无撕裂/绿块 |

### MSE（fMP4）通道在 MIUI WebView 上的实测结论：不可用

分片形态试过三种（每帧一分片 33ms / 200ms 一分片 / 只在关键帧切分至 CMAF 自解码单元）、播放点从 `buffered.start(0)` 起步、
只保留 2 秒窗口 `sb.remove`，结果一致：`readyState` 在 4↔2↔1 抖动、`currentTime` 长时间冻结（5.07s / 15.21s 不动）、
`buffered` 出现多段不相邻区间（`[2.97-7.77, 8.89-10.04, 12.33-15.75, 16.85-18.03]`）、rVFC 计帧多为 0。
服务端码流经 `ffmpeg` 验证完全正常（6.06s / 122 帧，1920×1080 与 720p 都能解出 PNG）。
服务端降到 720p30、480p20 亦无改善 → 判定为 MIUI WebView 的 MSE 实时追加解码能力问题，非码流问题。
处理：**MSE 默认关闭**，只有显式 `?mse=1` 才尝试；手机默认走 JPEG。

## 12. 桌面化改造与关闭行为（本轮）

### 12.1 构建与安装

| 环节 | 事实 |
| --- | --- |
| 编译 | `dotnet build` 通过（无警告中断）；曾修的三处编译问题：`Timer` 二义（改 `System.Windows.Forms.Timer`）、`ScreenRect` 无 `Index` 字段（改用 `GetMonitors()` 列表下标）、界面字符串里中文引号嵌套 |
| 发布体积 | publish 产物 `QDesktopExtension.exe` **≈179,939,587 B**（约 180MB）。比改前的约 93MB 大一倍，原因是 `UseWindowsForms` 带进了 `PresentationNative_cor3.dll`、`wpfgfx_cor3.dll`、`D3DCompiler_47_cor3.dll` 等 WPF 原生库（WinForms 的公共控件依赖 WPF 渲染栈） |
| 安装 | `install.ps1 -NoLaunch` 成功：装到 `C:\Users\Q\AppData\Local\Programs\QDesktopExtension`，建桌面 + 开始菜单快捷方式，写 HKCU 卸载项；覆盖安装保留 `appconfig.json` |
| 桌面快捷方式启动 | 进程 `MainWindowTitle = "QDesktopExtension — 远程桌面服务"`，`/status` 正常（实测 `29 fps, 4319 kbps, clients 1`），**没有 cmd 窗口** |
| 单实例 | 第二份实例（拷贝到 `%TEMP%\vds_test` 改端口 8777 的测试副本）起不来——被互斥体 `QDesktopExtension.Gui.Instance` 挡住并弹窗退出；顺带确认 `CloseMainWindow()` 后 6 秒内进程退出、端口关闭 |
| ps1 编码 | 第一次跑 `install.ps1` 报 `Unexpected token 瀹夎鍒?`：无 BOM 的 `.ps1` 被 Windows PowerShell 5.1 按 GBK 解码。补 **UTF-8 BOM** 后正常（本项目的固定坑） |

### 12.2 关闭面板的三选一（关键：弹窗能出来）

排查过程本身就是结论：先用 `PostMessage(WM_CLOSE)` 关闭时**弹窗始终不出现**，`ui.log` 里只有
`closing reason=TaskManagerClosing`——外部发来的 WM_CLOSE 被 WinForms 判成"任务管理器关闭"，
旧代码在 `FormClosing` 里直接 `ShowDialog` 会被关闭状态挡掉，于是走了"直接停服务退出"的分支。

改法（两者都要）：① 只有 `WindowsShutDown` 无条件直接停，`TaskManagerClosing` 也进询问分支；
② `e.Cancel = true` + 一次性 `Timer` 延后一帧再弹窗（不在 `FormClosing` 里直接开模态框）。

实测证据（`logs\ui.log`，进程 20880）：

```
23:23:54.199 closing reason=UserClosing running=True exit=False action=tray asking=False
```

```
23:21:02.808 closing reason=UserClosing running=True exit=False action=ask asking=False
23:21:02.873 ask: enter
23:21:02.942 ask: showing dialog
23:21:09.416 ask: dialog closed chosen=tray remember=True
```

- 弹窗确实出来了（`ask: showing dialog` → 7 秒后 `dialog closed`，是人在点），选了**只关闭面板**并勾了"记住"
- 生成 `uisettings.json` = `{"CloseAction":"tray"}`，之后再次关窗（23:23:54）直接 `action=tray` → 窗口 `Hide()`、进程存活、8767 继续服务
- **退出路径**：把 `uisettings.json` 置 `exit` 后重启再关窗 → `alive=0`，随后 `Invoke-RestMethod http://127.0.0.1:8767/status` 失败（**port closed**），端口与管线都释放干净
- 恢复默认 `ask` 以便下次关窗仍会询问

### 12.3 客户端"兼容模式"提示自动消失（真机复验）

`showNotice(msg, holdMs)` 加 6 秒定时器，重启服务后在小米 21091116C（MIUI WebView，走 JPEG 兼容通道）
上用 `adb exec-out screencap` 抓屏对比：

| 时间点 | 画面 |
| --- | --- |
| 打开页面后约 4s | 视频顶部仍有橙色提示条（兼容模式说明） |
| 打开页面后约 12s | 提示条已消失，画面直接从浏览器标题栏下方开始，无残留遮挡 |

### 12.4 本轮新增的两个文件约定

| 文件 | 内容 | 说明 |
| --- | --- | --- |
| `uisettings.json`（程序目录） | `{"CloseAction":"ask|tray|exit"}` | 纯界面偏好；读坏/缺失都回落到 `ask`，不影响启动 |
| `logs\ui.log` | 每次关窗一行 `closing reason=… running=… action=…`，询问流程另有 `ask:` 行 | 只用于确认关闭行为，量极小；排障时可删，程序会重建 |

## 13. 仍未验证的部分

| 项 | 原因 |
| --- | --- |
| 浏览器里 WebCodecs 的实际解码 | 手头只有 MIUI WebView（无 WebCodecs）；H.264 路径靠 ffmpeg 解同一条码流验证 |
| 键盘输入落到具体窗口的效果 | 已按协议 + 注入返回值验证，缺少可视目标窗口 |
| Ctrl+C 优雅退出 | 后台启动的进程无法发送控制台 Ctrl+C |
| 多显示器切换的真实画面 | 本机只有 1 台显示器，切换路径实测到"重建采集+编码+通知"这一步 |
| 2.4G Wi-Fi 下的 JPEG 画质/帧率 | 本轮手机在 5G 频段；JPEG 25fps 约 1.6MB/s，2.4G 上需把 `JpegWidth`/`JpegFps` 下调 |


## 13. 复制屏幕 / 扩展屏（真机验证）

环境：Windows 11 + 2240×1400 单屏（150% 缩放），手机 Redmi 21091116C（MIUI WebView，JPEG 通道）。

1. **复制屏幕**：`/status` 返回 `source={"kind":"monitor","index":0,"w":2240,"h":1400}`，手机网页显示完整桌面（任务栏、图标可见）。实测 30fps。
2. **扩展屏**：`appconfig.json` 配 `UseVirtualScreen=true` + `VirtualWindows` 3 个程序（豆包 / 360 浏览器 / 资源管理器），重启后 `/status` 返回 `source={"kind":"virtual","w":1920,"h":1080}`；用 WS 客户端切 `{"type":"source","kind":"virtual"}`，存下的 JPEG 是「左半整列 + 右半上下」三格合成画面（每格内容各自不同，无任务栏）。
3. **输入映射**：`{"type":"input","ev":"down","x":0.25,"y":0.5}` → 日志 `虚拟屏输入: (0.250,0.500) → 窗口「项目审查与开发 - 豆包」坐标 (1120,664)`；`(0.75,0.25)` → 浏览器窗口；`(0.75,0.75)` → 资源管理器窗口。即"点哪个格子就注入到哪个程序"。
4. **手机端下拉**：`auth_ok` 带回 `virtual:3`，网页「采集源」里出现「扩展屏 · 3 个程序」一项；切过去后手机截图确认画面变成三格合成图，切回「复制屏幕」恢复桌面画面。
5. **空配置兜底**：`VirtualWindows` 为空时下拉只有"复制屏幕"选项；若成员窗口全关，管线每秒巡检一次并回退到显示器，客户端不会长时间黑屏。

6. **空扩展屏**：`VirtualWindows` 为空、`UseVirtualScreen=true` 启动，`/status` 仍返回 `source={"kind":"virtual","w":1920,"h":1080}`，抓帧是一块深灰底 + "扩展屏已就绪 / 1920 × 1080 / 在手机上选择「投放程序」，或点电脑面板的「配置扩展屏…」"提示画面（5.6KB JPEG），手机网页打开即显示这块空屏，不需要任何预先选定的程序。
7. **运行时投放/清空**：WS `{"type":"virtualadd","hwnd":329660}` → 日志 `扩展屏投放程序: 「项目审查与开发 - 豆包」 共 1 个`，随后抓帧 49.7KB（该程序铺满画布）；`{"type":"virtualclear"}` → 回到 5.6KB 的空屏画面。即扩展屏可以"先空着，用的时候再往上放程序"。

8. **对面那块屏的分辨率**：客户端连上后 sends `{"type":"viewport","w":1080,"h":2340}`，日志 `扩展屏尺寸跟随客户端: 1080x1600`（超上限按 clamp），JPEG 帧头实测 960×1422（竖屏比例），说明扩展屏是按对面设备的真实屏幕出画的，不是把 1920×1080 拉伸。
9. **空扩展屏的桌面观感**：0 个成员时画布铺主屏壁纸（`SystemParametersInfoW(SPI_GETDESKWALLPAPER)`）+ 底部任务栏条 + 中央提示，真机截图与「扩展屏已就绪（这块屏就是你的第二块显示器）」一致；即"不选程序也是一块屏"。

10. **客户端零选择**：`index.html` 的工具栏只剩「状态 / 全屏」，采集源与投放选择器已移除；显示模式（复制 / 扩展）只在电脑面板设置。真机截图确认页面只有画面，无下拉。
11. **窗口"搬到扩展屏"= 真的移出主屏**：Notepad `(123,51,1165,725)` → 投放后 `(1533,51,1165,725)`（移出 2240 宽的主屏右侧），清空后回到 `(123,51,1165,725)`；与拖到第二块显示器效果一致。
