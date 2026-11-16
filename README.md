# QDesktopExtension · 桌面扩展平台

把 Windows 电脑的画面实时投到手机、平板或另一台电脑的**浏览器**里，并把触摸 / 鼠标 / 键盘 / 手写笔操作反向注入回这台电脑。
不需要在客户端装 App，也不需要显示器或采集卡 —— 浏览器打开网页就能用。

作者：**Misite齊** · <https://github.com/MisiteQ>

---

## 特性

| 分类 | 能力 |
| --- | --- |
| 抓屏 | GDI `BitBlt` + DIB 双缓冲，独立采集线程；可选某块显示器、全部显示器拼接、或单个窗口；可把本机鼠标指针画进画面 |
| 编码 | FFmpeg `libx264`（BGRA → YUV420P），输出带 `avcC` extradata 的裸 H.264，直接喂给浏览器 WebCodecs |
| 传输 | WebSocket 二进制帧，首字节标记类型：`0` 增量帧 / `1` 关键帧 / `2` JPEG / `3` fMP4 分片 / `4` JSON 控制消息 |
| 解码 | 优先 **H.264（WebCodecs）**；显式加 `?mse=1` 试 **MSE + fMP4**；两者不可用时自动走 **JPEG** 兼容通道 |
| 多设备 | 每台设备单独一块**虚拟扩展屏**（Parsec VDD），最多 3 块，各自独立采集 / 编码 / 发帧，互不抢帧 |
| 反向输入 | 鼠标绝对坐标（多屏正确）、滚轮、`KEYEVENTF_UNICODE` 键盘（中文与符号不依赖目标机布局）、笔压注入；断线自动松开残留按键 |
| 自适应 | 网络差时自动降码率 / 降分辨率（窗口采样 + 冷却 + 迟滞，不来回抖）；新客户端接入立即补发关键帧，不用等下一个 I 帧 |
| 安全 | JSON 鉴权 + 定长比较、IP 黑白名单（支持 `192.168.1.*` 与 CIDR）、单客户端带宽配额、`/status` 需访问密码 |
| 稳定性 | 配置损坏自动备份为 `.corrupted-时间戳` 并重建默认值，不会因配置问题启动失败；编码分辨率统一压在 1920×1080（H.264 Level 4.0），避免手机硬解被拒 |

## 运行要求

- Windows 10 / 11，x64
- 运行时**不需要**另装 .NET（发布产物是自包含单文件 exe，内嵌 WinForms 依赖）
- 虚拟扩展屏为可选功能，需要安装 Parsec Virtual Display Driver（仓库自带 `install-parsec-vdd.ps1` / `driver\`）

## 快速开始

### 方式一：安装包

双击 `dist\QDesktopExtension-Setup.exe`，按提示装到当前用户目录（不弹 UAC），桌面与开始菜单会生成快捷方式。

### 方式二：源码构建

```powershell
# 需要 .NET 8 SDK（只有运行时无法编译）
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
cd .\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish
powershell -ExecutionPolicy Bypass -File .\install.ps1          # 装到 %LOCALAPPDATA%\Programs
# 可选参数：-AutoStart（开机自启） / -Scope Machine（装到 Program Files，需管理员）
```

`build-release.ps1` 会依次完成：`dotnet publish -c Release` → 把 FFmpeg 动态库复制进 `publish\ffmpeg` → 收集安装 / 卸载 / 驱动脚本。

### 连接

1. 双击桌面图标启动，服务会**自动开始串流**，面板里显示访问地址；
2. 手机 / 平板 / 另一台电脑的浏览器打开 `http://<本机局域网IP>:8767`；
3. 输入访问密码（默认 `123456`，**请立刻修改**）；
4. 连上后画面上会出现状态仪表与控制按钮，右下角按钮可展开方向、铺满、全屏等开关。

> 面板「连接访问」页可以切换「局域网地址 / USB 数据线 / 两种都允许」。USB 方式需手机开启 USB 调试并授权本机，
> 由 `adb reverse` 把端口映射到手机本地。

## 界面

设置面板共五个页签：

- **显示采集** —— 复制屏幕 / 扩展屏、采集来源、虚拟扩展屏状态、光标与远程输入开关
- **连接访问** —— 访问方式、USB 状态、网页端口 / 推流端口 / 访问密码、仅本机访问
- **画质性能** —— 帧率、码率、画面尺寸上限、自适应、JPEG 兼容通道参数
- **系统维护** —— 开机自启、关闭窗口行为、日志详细程度
- **关于** —— 版本、作者、项目地址、运行环境、开源组件

托盘菜单同样提供「打开设置面板 / 打开网页 / 启动停止服务 / 关于 / 退出」。

## 配置（appconfig.json）

程序目录下的 `appconfig.json`，改完在面板里「保存并重启服务」生效。常用项：

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `Password` | `123456` | 访问密码，同时是 `/status`、`/shutdown` 的 token |
| `BindAddress` | `0.0.0.0` | 监听地址；改成 `127.0.0.1` 即只允许本机 |
| `HttpPort` / `WsPort` | `8767` / `8765` | 网页端口 / WebSocket 推流端口 |
| `Fps` / `BitRate` | `30` / `8000000` | 共享流帧率与码率（bps） |
| `JpegFps` / `JpegWidth` | `25` / `960` | JPEG 兼容通道帧率与画面宽度 |
| `CaptureMonitor` | `0` | 采集第几块显示器 |
| `DrawCursor` | `true` | 是否把鼠标指针画进画面 |
| `EnableAdaptive` | `true` | 自适应码率 / 分辨率 |
| `AllowRemoteInput` | `true` | 允许浏览器反向操作电脑 |
| `LogLevel` / `LogPath` | `Info` / `./logs` | 日志级别与目录（滚动、限大小） |
| `VirtualWindows` | `[]` | 每客户端虚拟屏的窗口摆放记录 |

配置写坏时（例如标题里混入控制字符导致 JSON 非法）程序会把它备份成 `appconfig.json.corrupted-<时间戳>` 再生成默认配置，不会启动失败。

## 手机 / 平板兼容性

手机浏览器通过 `http://<IP>:8767` 访问属于**非安全上下文**，Chrome 会隐藏 `VideoDecoder`（WebCodecs），
页面会自动走 JPEG 兼容通道（默认 25fps / 960px 宽），操作手感可用，画质与帧率低于 H.264。

想在有 WebCodecs 的浏览器上跑 H.264：

- Android 版 Chrome 自带 WebCodecs，但要求安全上下文 —— 用 `adb reverse tcp:8767 tcp:8767`（推流端口同理）
  后在手机上打开 `http://localhost:8767` 即可；
- 或者给服务配 HTTPS 证书（当前未内置，属后续计划）。

若 H.264 解码器配置失败，页面会在数秒内自动降级到 JPEG，并记入 `localStorage`（1 小时内不再反复尝试）；
需要强制重试时在地址后加 `?h264=1`。

## 命令行

```
QDesktopExtension.exe                 托盘 + 设置面板（推荐）
QDesktopExtension.exe --console        控制台模式
QDesktopExtension.exe <配置.json>     指定配置以控制台模式运行
QDesktopExtension.exe --selftest       只跑合成图案的采集/编码自检
QDesktopExtension.exe --help           帮助
```

HTTP 端点：`/`（内置页面）、`/health`（存活探针）、`/status`（运行状态，远程需 `?token=<Password>`）、
`/windows`（窗口列表）、`/shutdown`（停机）。

## 目录结构

```
QDesktopExtension.csproj     项目文件（版本号、作者等元信息）
Program.cs                   入口、命令行、单实例
TrayApp.cs                   托盘常驻与应用生命周期
MainForm.cs                  设置面板
StreamServer.cs              WebSocket 服务、鉴权、输入事件接收
StreamPipeline.cs            采集 → 编码 → 分发的共享管线
PipelineHost.cs              每客户端独立管线
ClientScreens.cs             每客户端虚拟扩展屏管理
ParsecVdd.cs                 Parsec 虚拟显示驱动封装
DisplayCapture.cs / CaptureWorker.cs   抓屏
VideoEncoder.cs / Fmp4Muxer.cs / JpegStreamEncoder.cs   编码与封装
SimpleHttpServer.cs          内置 HTTP 服务（页面 + 探针）
UiTheme.cs                   浅色主题与自绘控件
Resources/index.html         浏览器客户端（单文件，无外部依赖）
install*.ps1 / build-release.ps1 / uninstall.ps1   安装与构建
docs/NOTES-历史调研记录.md    开发过程中的实测记录与取舍（含 VDD 长尾问题）
```

## 安全须知

- 默认 `123456` + `0.0.0.0` + 允许远程输入，等于**同网段任何人都能操作这台电脑**。请先改强密码；
  不需要局域网接入时把监听地址改成 `127.0.0.1`，或勾选「只允许本机访问」。
- 传输是明文 `ws://` / `http://`，不要在公共网络直接开放；需要外网访问请走 VPN / SSH 隧道。
- `/status` 已要求访问密码（本机回环放行），`/windows` 等接口仍以密码作为 token。

## 开发

```powershell
dotnet build -c Debug      # 输出到 bin\Debug\net8.0-windows10.0.19041.0\win-x64\
dotnet publish -c Release  # 单文件自包含发布
```

> 若系统 PATH 上的 `dotnet` 只有运行时，请把 `%LOCALAPPDATA%\Microsoft\dotnet` 前置到 PATH，
> `build-release.ps1` 会自动在当前用户目录找 SDK。

## 许可与致谢

本项目为个人作品，按现状提供，使用前请自行评估远程访问带来的安全风险。

第三方组件：Fleck（WebSocket 服务端）、FFmpeg / FFmpeg.AutoGen（H.264 编解码与封装）、
Parsec Virtual Display Driver（虚拟扩展屏）。
