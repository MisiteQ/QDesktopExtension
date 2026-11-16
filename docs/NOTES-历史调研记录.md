# QDesktopExtension

Windows 屏幕串流服务：本机抓屏 → H.264 编码 → WebSocket 推流，浏览器直接用 WebCodecs / MSE / JPEG 解码，并可把鼠标/触摸/笔输入反向注入回本机。

## 功能

1. GDI BitBlt + DIB 双缓冲抓屏（DPI 感知，可选中任意显示器或整个虚拟桌面），独立采集线程，可把光标画进画面
2. FFmpeg 编码：BGRA → YUV420P → libx264，输出 avcC extradata 直接给 WebCodecs / fMP4 用（H.265 尚未接入 hvcC，配置里会被回落成 H.264）
3. 传输：WebSocket（每帧带 1 字节关键帧标记）
4. 码率/分辨率自适应（窗口采样 + 冷却 + 迟滞，不来回抖）
5. 新客户端接入立即发送缓存的关键帧，不用等下一个 I 帧
6. 鉴权（JSON auth，定长比较）、IP 黑白名单（支持 `192.168.1.*` 与 CIDR）、单客户端带宽配额
7. 输入注入：鼠标（SendInput 绝对坐标，多屏正确）、滚轮、笔压（`InjectSyntheticPointerInput`，可开关）
8. **键盘注入**：可打印字符走 `KEYEVENTF_UNICODE`（不依赖目标机键盘布局，中文/符号可直接打），功能键与组合键走虚拟键码；断线自动松开残留按键
9. **运行中切换显示器**：客户端下拉框切换，服务端重建采集/编码并让所有客户端重新配置解码器
10. 滚动日志、`/status` 运行状态探针（含显示器、输入事件计数、采集/编码耗时）

## 手机 / 平板访问（重要）

手机上通常是 `http://<本机IP>:8767`，这是**非安全上下文**，浏览器不给 WebCodecs（`VideoDecoder`），
所以页面默认走 **JPEG 兼容通道**：独立线程出图，`JpegFps`（默认 20，实测 25 也稳）、`JpegWidth` 960，
鼠标键盘照常可用。手机端实测渲染 20–25fps、端到端延迟约 10–70ms，刷视频基本跟手。
页面顶部会显示一条橙色提示说明当前处于兼容模式，**约 6 秒后自动消失**（也可以点一下提前收起），
不会一直压在画面上。

> 实测：小米/红米自带的"浏览器"（MIUI WebView，`Chrome/109`）**没有 WebCodecs**，必然走 JPEG 通道；
> 这种浏览器对 `http://localhost:8767`（adb reverse 场景）还会报"网页打开失败"，手机上直接用局域网 IP 更省事。

`?mse=1` 会尝试 **MSE + fMP4** 通道（`video/mp4; codecs="avc1..."`）：服务端已实现分片封装，
但**同一台 MIUI WebView 上实测无法稳定出画**（`readyState` 4↔2↔1 抖动、`currentTime` 冻结、
`buffered` 出现多段不连续区间），所以默认关闭，只留给桌面 Chrome 或别的设备试。

想要手机也跑 H.264 30fps，只有两条路：

1. **装 Chrome**（Android 版自带 WebCodecs）：配合 `adb reverse tcp:8767 tcp:8767` + `tcp:8765`
   打开 `http://localhost:8767`（`localhost` 属安全上下文），就能走 H.264 30fps；
2. **HTTPS**：给服务配自签证书，手机首次打开时手动信任（尚未内置，属于后续计划）。

如果 H.264 通了但手机仍黑屏，页面会在 4 秒内自动降级到 JPEG 兼容通道，不会一直黑着。

## 一键打包与安装（正式运行）

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
cd .\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish
powershell -ExecutionPolicy Bypass -File .\install.ps1          # 装到自己用户目录（不需要管理员）
```

`build-release.ps1` 依次：`dotnet publish -c Release` → 把 FFmpeg DLL 拷进 `publish\ffmpeg` →
复制 `install.ps1` / `uninstall.ps1` 并写 `stop.ps1`，产物目录：

```
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
├─ QDesktopExtension.exe   单文件、self-contained，不装 .NET 也能跑（约 180MB，含 WinForms 相关原生库）
├─ appconfig.json               配置（首次可改密码）
├─ ffmpeg\                      运行所需的 FFmpeg 8.1 动态库
├─ install.ps1                  安装（快捷方式 + 卸载项 + 可选开机自启）
├─ uninstall.ps1                卸载（-Quiet 静默）
├─ stop.ps1                     命令行兜底停机
└─ logs\                        滚动日志（含 ui.log：面板关闭行为的记录）
```

`install.ps1` 做的事：

- 默认 `-Scope User` 装到 `%LOCALAPPDATA%\Programs\QDesktopExtension`；`-Scope Machine` 需要管理员，装到 `Program Files`
- 装前先停掉旧实例；覆盖安装时**保留已有的 `appconfig.json`**（不会把你的密码/参数冲掉）
- 建桌面快捷方式与开始菜单快捷方式
- 写 `HKCU\...\Uninstall\QDesktopExtension` 卸载项（能在"应用/卸载"列表里看到）
- `-AutoStart` 写 `HKCU\...\Run` 实现开机自启；`-NoLaunch` 装完不自动启动
- 卸载：`uninstall.ps1`（或系统里的卸载入口），会停进程、删快捷方式、删 Run 项与卸载项，再删安装目录

装好之后**只有一个图标**：双击桌面快捷方式就开始串流并弹出控制面板，没有 cmd 窗口。

## 准备

1. 运行只需要 Windows 10 1809+ / Windows 11（发布包是 self-contained，不需要装 .NET）
2. **编译**才需要 .NET 8 SDK（仅运行时不够）
3. **FFmpeg 8.1 动态库**放到程序目录的 `ffmpeg` 子目录（`avcodec-62.dll`、`avformat-62.dll`、`avutil-60.dll`、`swscale-9.dll`、`swresample-6.dll` 等）
   - 下载：<https://github.com/BtbN/FFmpeg-Builds/releases> 选 `ffmpeg-n8.1.x-win64-gpl-shared-8.1.zip`，解压后把 `bin/*.dll` 拷进去
   - 绑定版本为 `FFmpeg.AutoGen.Bindings.DynamicallyLoaded 8.1.0`，DLL 主版本必须与之一致
4. 放行端口：8767（HTTP 页面）、8765（WebSocket）；连接方式选「USB 数据线」时只监听回环地址，局域网连不上
5. `appconfig.json` 里的 `Password` 默认是 `123456`，正式用之前务必改掉；
   `BindAddress` 为 `0.0.0.0` 时同网段任何人都能连页面

## 运行与停止

**装好后（推荐）**：双击桌面图标 `QDesktopExtension` → 自动开始串流 + 弹出控制面板，同时在托盘常驻。

- 面板顶部状态区显示 `29 fps ・ 399 kbps ・ 客户端 2 个 ・ 采集 正常 ・ 已运行 31s`，访问地址是可点的链接
- 参数页：显示模式（复制/扩展、采集哪块屏、`配置扩展屏…`、画光标、允许远端输入）／画面质量（帧率、码率、分辨率上限、自适应）／连接方式（局域网 / USB 数据线，以及 adb 状态）／日志与自启
- 底部：`保存配置`（只写盘）／`保存并重启服务`（写盘后重建管线）
- 托盘图标：双击开面板，右键菜单可 `打开设置面板 / 打开网页 / 停止服务 / 退出`

**关闭窗口时的行为**（高级页里可选，也可在弹窗里"记住我的选择"）：

| 选项 | 表现 |
| --- | --- |
| 每次询问（默认） | 弹三选一：只关闭面板（后台继续串流）／退出程序（停止串流并释放端口）／取消 |
| 只关闭面板 | 窗口收进托盘，串流继续；双击托盘可再打开 |
| 退出程序并停止服务 | 先把管线停干净、释放 8765/8766/8767，再退出进程 |

选择记在程序目录的 `uisettings.json`（`{"CloseAction":"ask|tray|exit"}`），与 `appconfig.json` 分开，
不会污染服务/手机端用的参数。系统关机、注销时不再弹框，直接停服务退出。

**命令行/旧用法仍然保留**（脚本、CI、排障都能用，但会开一个控制台窗口）：

```powershell
QDesktopExtension.exe                     # 不带参数 = GUI 模式
QDesktopExtension.exe --console            # 老的控制台模式，Ctrl+C 退出
QDesktopExtension.exe --selftest out.h264  # 编码自检
QDesktopExtension.exe --help               # 帮助
QDesktopExtension.exe appconfig.json       # 指定配置文件的控制台模式
stop.ps1                                        # 等于 GET /shutdown?token=<密码>
```

启动后日志会打印地址：

- 浏览器打开 `http://<本机IP>:8767/` → 输入密码即可看到画面，鼠标/键盘/笔均可反向操作
- 状态：`http://<本机IP>:8767/status`；存活探测：`/health`
- 退出码：`0` 正常退出、`1` 配置读取失败、`2` 采集初始化失败、`3` 编码器初始化失败、`4` 端口/服务启动失败

## 自检（不依赖屏幕内容）

排查 FFmpeg 依赖 / 编码链路时不用真的开浏览器：

```powershell
QDesktopExtension.exe --selftest selftest.h264
```

会生成合成图案并编码 90 帧，日志打印 `codec`、`extradata` 字节数与关键帧数量；
落盘的 `.h264` 可直接用 ffmpeg 解码核对颜色与方向：

```powershell
ffmpeg -i selftest.h264 -frames:v 1 frame.png
```

## 审查与验证记录

- `REVIEW.md`：原项目逐条问题清单（含阻塞级缺陷）与本次修复对照表
- `VERIFY.md`：构建、编码自检、运行/协议实测记录与未覆盖项

## 配置（appconfig.json）

| 键 | 说明 |
| --- | --- |
| `Password` | 访问密码 |
| `BindAddress` | 监听地址，`0.0.0.0` 局域网可访问，`127.0.0.1` 仅本机 |
| `WsPort` / `HttpPort` | WebSocket / 网页端口 |
| `Fps` / `BitRate` / `Codec` | 帧率 / 起始码率 / 编码器（目前只支持 `H264`） |
| `KeyFrameIntervalSec` | 关键帧间隔（秒） |
| `EncoderThreads` | 编码线程数，0 = 自动 |
| `EncoderPreset` | x264 预设，默认 `ultrafast`（低延迟低占用；想提画质可改 `veryfast`） |
| `JpegFps` / `JpegWidth` | JPEG 兼容通道帧率 / 画面宽度（默认 25 / 960；实测 25fps 手机端渲染 23–25fps，带宽约 1.4–1.6MB/s，2.4G Wi-Fi 建议降到 15fps） |
| `CaptureMonitor` | 显示器序号，`-1` = 全部显示器；客户端运行时也能切 |
| `DrawCursor` | 是否把本机光标画进画面 |
| `EnableAdaptive` / `Min*` / `Max*` | 自适应码率与分辨率上下限 |
| `ClientMaxBitRate` | 单客户端带宽上限（bit/s） |
| `BlackList` / `WhiteList` | IP 名单，白名单非空时只放行名单内 |
| `AllowRemoteInput` | 允许远端输入注入 |
| `EnableWindowsInk` | 是否注入真实笔压（失败自动回退鼠标） |
| `LogLevel` / `LogPath` / `LogFileMaxSizeMB` / `LogFileCount` | 日志 |

## 已知限制

- GDI 抓屏无法采集独占全屏 / 硬件覆盖层画面（需要 DXGI Desktop Duplication 或 WGC）
- GDI 抓屏单帧开销与分辨率强相关：本机 2240×1400 实测约 33ms/帧（虚拟显示驱动 + 高 DPI），
  因此实际帧率约 22–30fps；普通显卡驱动环境下通常 5–10ms，可跑满 30fps
- H.265 暂未支持：缺少 hvcC 描述符与对应 fMP4 封装，配置里写 `H265` 会被自动回落成 H.264
- 键盘会经过浏览器：`Ctrl+W`/`Ctrl+T`/`Alt+Tab`/`Win` 这类系统或浏览器保留组合键拦不到，
  `Ctrl+Alt+Del` 属于安全桌面，任何用户态程序都无法注入
- 锁屏 / 独占全屏 / 切换显示模式时抓屏会失败：此时不推陈旧画面，`/status` 的 `capturing` 变 `false`，
  网页 HUD 会提示"远端屏幕当前无法采集"，恢复后自动继续
- 通过 `http://局域网IP` 打开页面属于非安全上下文，浏览器不会给剪贴板/IME 等高级权限
  （`localhost` 不受限）

## 复制屏幕 / 扩展屏（两种用法，都不需要虚拟显示驱动）

面板「高级」页的「采集源」下拉里有两类选项：

| 选项 | 含义 | 实现 |
| --- | --- | --- |
| 复制屏幕 · 显示器 N | 手机/平板显示电脑当前画面 | 直接抓该显示器的屏幕区域 |
| 复制屏幕 · 全部显示器 | 多屏时把整块虚拟桌面一起推过去 | 抓整块虚拟桌面 |
| 扩展屏 · 手机当第二块屏 | 手机/平板成为电脑的第二块屏 | 软件合成画布（见下） |

**扩展屏怎么做到不依赖驱动**：点「配置扩展屏…」勾选 1~4 个程序，服务端把它们拼成一块
1920×1080 的画布（`VirtualScreen.cs`）——1 个铺满、2 个左右分屏、3 个左边整列 + 右边上下、
4 个 2×2。手机打开网页后在「采集源」里选「扩展屏 · N 个程序」，看到的就是这块画布；
点/划哪个格子，输入就按该格子的归一化坐标反算成对应程序客户区的坐标再注入，格子之间的
空白处不注入。按下前会先把目标程序窗口提到最前，避免点击被上层窗口吃掉。

这样任何一台只装了 Windows 的电脑都能用扩展屏，不用装 IddCx 虚拟显示器 / 虚拟显示适配器，
也不用改系统显示设置。

**分辨率与性能**：画布尺寸可在「配置扩展屏」里改（默认 1920×1080）。每帧只做一次桌面区域
拷贝（覆盖所有成员窗口的并集），再按格子缩放（缩放本身约 0.6ms）；实测本机两三个程序时
采集约 60~100ms/帧、编码 8~30ms，客户端断点续连时约 12~20fps，跟复制屏幕的节奏接近。

### 扩展屏是"先有屏，再放东西"

和真的再插一块显示器一样：**扩展屏本身不需要任何预先选定的程序**。
选「扩展屏」后手机看到的是一块属于它的桌面（1920×1080 的空屏，屏中央有一行提示），
随后：

- 手机上「采集源」下拉里选「扩展屏」，工具栏里会多出「投放程序」下拉：选一个程序就把它搬上扩展屏（最多 4 个，自动分格），选「清空投放」回到空屏；
- 也可以在电脑面板「配置扩展屏…」里预先放好 1~4 个程序（勾 0 个就是空屏，会提示确认）；
- 成员程序被关掉时，那一格自动消失，剩下程序重排；全部关掉就回到空屏。

因此"用户不选任何程序也能正常显示"是成立的——它本来就是一屏桌面，只是还没有窗口放上去。

## 「浏览器连上来的那块屏」就是一台新显示器

系统不区分对面是手机、平板还是另一台电脑 —— 任何设备用浏览器打开 `http://<本机IP>:8767/`，
那块屏幕就会被当成电脑新增的一块显示器。连上之后在「采集源」下拉里选：

| 选项 | 相当于 Win+P 的 | 手机/pad/另一台电脑上看到 |
| --- | --- | --- |
| 复制本机屏幕 N（w×h） | **复制** | 电脑当前画面原样镜像 |
| 扩展本机屏幕 · 空桌面 / 上面放了 N 个窗口 | **扩展** | 一块属于它的桌面：壁纸铺满、底部任务栏条；把窗口放上来才有内容 |

要点：

- **扩展屏不需要预先选程序**。选「扩展本机屏幕」后立刻有画面（桌面壁纸），和插上一块新显示器看到桌面是一个意思；只有想在这块屏上放东西时才去「投放程序」。
- **这块屏的分辨率跟着对面那块屏走**：客户端连上时会回报自己的可视尺寸，服务端按它重建画布（宽 640~2560、高 360~1600），所以手机竖屏得到 1080×1600 这类竖向画面，不会把 1920×1080 硬拉伸变形。
- 客户端数量不限于一：两台手机、一台 pad、另一台电脑各开一个网页，就是三块"显示器"，互不冲突；每块都可以独立选复制或扩展。

### 浏览器端不做任何选择：它就是一块显示器

连接进来的设备（手机、平板、另一台电脑的浏览器）只是"那块屏"，页面上只有「状态」「全屏」两个按钮，
没有任何"选复制还是扩展""选哪个程序"的入口 —— 这些全部在**电脑上的设置面板**里定：

- 面板「高级 → 采集源」：`复制本机屏幕 N` = 复制（Win+P 的复制）；`扩展屏 · …` = 扩展（Win+P 的扩展）；
- 面板「配置扩展屏…」：往扩展屏里放 1~4 个程序，或一个都不放（空桌面）。

于是"换个手机""换台 pad""换另一台电脑的浏览器"都不用改任何设置，连上就是同一块显示器。

## 可选：装一块"系统认得出"的虚拟显示器（真扩展屏）

上面的「软件扩展屏」是零驱动方案：画面由程序合成，系统不知道自己多了一块屏。
要做到和物理显示器**完全一致**（系统显示设置里能看到这块屏、能拖动它的相对位置、单独设分辨率、
窗口直接用鼠标拖过去），需要在系统里注册一块虚拟显示器 —— 这一步必须由驱动完成。

安装包已自带：`driver\VirtualDisplayDriver\`（MttVDD 25.7.23，**已签名的 IddCx 间接显示驱动**，
来源 https://github.com/VirtualDrivers/Virtual-Display-Driver）。

安装（一次即可，需要管理员）：

```powershell
# 右键 install-vdd.ps1 → 用 PowerShell 运行；会自动弹 UAC 提权
\.\install-vdd.ps1
```

装完会发生什么：

1. Windows「设置 → 系统 → 显示」里多出一块显示器（编号看系统给的，可能是 2 号或别的号）；
2. 在这页里可以把它拖到主屏的上/下/左/右，设成"扩展"，并单独设置分辨率和缩放；
3. 窗口可以直接用鼠标拖到那块屏上，任务栏也可以延伸到它（和真显示器一样）；
4. 程序面板「高级 → 采集源」里选「**扩展屏 · 显示器 N**」，手机/pad 看到的就是这块真实扩展屏的画面；
5. 想撤掉：`pnputil /delete-driver oem<xx>.inf /uninstall`，或在设备管理器里删掉该显示器。

不装驱动也能用：面板里「**扩展屏 · 软件扩展屏**」就是那个零驱动的画布方案（空桌面 / 放 1~4 个程序）。

### 每台设备一块独立扩展屏：Parsec VDD

发布目录还自带 `driver\ParsecVDD\parsec-vdd-0.45.0.0.exe`（官方签名，来源
https://github.com/nomi-san/parsec-vdd）。运行 `install-parsec-vdd.ps1` 安装后，
H.264 客户端接入时会自动创建一块独立的虚拟显示器，断开时自动移除；日志出现
「Parsec VDD 已就绪」即表示可用。没装这个驱动时自动退化为所有设备共享采集画面。

### 虚拟显示驱动：GitHub 上的关键事实（决定能不能"装上就出现屏"）

来源：VirtualDrivers/Virtual-Display-Driver 的 README、Community Scripts/silent-install.ps1，以及 issue #364。

1. **驱动会把配置文件路径写死**：`C:\VirtualDisplayDriver\vdd_settings.xml`。
   驱动文件只放进 DriverStore 或程序目录时，它读不到这份配置，"设备正常但没有显示器"就是这么来的。
   → 安装脚本第一步就是把 `VddSettings` 那套文件复制到 `C:\VirtualDisplayDriver\`。
2. **建根设备要用 nefconw 或 SetupAPI**：`pnputil /add-driver` 只把驱动包放进驱动库，**不会创建显示器设备**。
   官方社区脚本用的是 `nefconw.exe install MttVDD.inf "Root\MttVDD"`。
3. **一般不需要重启**：IddCx 是 UMDF 用户态驱动，设备建立后立即加载；改完只需 `pnputil /restart-device "ROOT\MttVDD"`。
4. **唯一确实需要重启的情况**：HAGS（硬件加速 GPU 计划）开着。issue #364 里有人就是关掉 HAGS 重启一次后显示器才出现。
   注册表 `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`：2=开，1=关，未设置=关。
5. **设备其实建好了却没屏**：设备管理器里对该设备右键「启用设备」，或系统设置里点「检测」。

### 实测：设备已装上但屏被系统撤掉（DXGI_ERROR_ACCESS_LOST）

本机（Windows 11 26100、Intel Iris Xe）实测结果，作为后续排查基线：

- `pnputil /add-driver /install` → 得到 `oem116.inf`
- `nefconw install MttVDD.inf "Root\MttVDD"` → `Device and driver installed successfully`
- 设备 `ROOT\DISPLAY\0001`「Virtual Display Driver」`Started`、问题码 0、`UpperFilters={IndirectKmd}`
- 驱动日志 `C:\VirtualDisplayDriver\Logs\log_*.txt`：
  `Using vdd_settings.xml` → `ASSIGNED GPU: Intel(R) Iris(R) Xe Graphics` → `Monitor arrival successfully reported`
  → 紧接着 `Failed to set device to swap chain. HRESULT: 0x887A0026`（`DXGI_ERROR_ACCESS_LOST`）
  → `Unassigning swap chain`，`Screen.AllScreens.Count` 始终为 1

已逐项排除（都重试过，报错完全不变）：

| 尝试 | 结果 |
| --- | --- |
| HAGS 开启（`HwSchMode` 未设置=关） | 不是原因 |
| `<HardwareCursor>false</HardwareCursor>` | 仍 `0x887A0026` |
| 只保留 1920×1080@60 单一模式 | 仍 `0x887A0026` |
| `pnputil /restart-device` 重启设备 | 仍 `0x887A0026` |
| 停用 AskLink IDD（`ROOT\DISPLAY\0000`）排除 IDD 冲突 | 仍 `0x887A0026` |
| `<friendlyname>Microsoft Basic Render Driver</friendlyname>` | 仍 `0x887A0026`（日志里 `Adapter found` 已变为 Microsoft 适配器） |

### 结论（已解决）：`ACCESS_LOST` 是设备启动瞬间的瞬时状态，不是永久失败

继续排查后实测成功，机器上真的多出了第二块显示器：

```text
before start: 2
after app start: 2
  \\.\DISPLAY1  2240x1400 @(0,0)
  \\.\DISPLAY46 2880x1620 @(3360,0)   ← 虚拟显示驱动产生的扩展屏
```

`pnputil /restart-device "ROOT\DISPLAY\0001"` 之后再读 `Screen.AllScreens.Count` 就变成 2，反复重启设备、启动本程序后都保持 2。
也就是说：`Failed to set device to swap chain` 只是驱动每次启动头几百毫秒内的重试过程（日志里同一时刻连续出现多条
`Failed to acquire buffer` / `Failed to set device`），Windows 随后自己重试成功并保住了这台显示器；
之前判断“驱动装不出屏”是错的——那时是**每次都在它还没重试成功的时候就去读屏数**。

**可复现的稳妥顺序**（装好驱动后照做即可）：

```powershell
# 1. 停掉正在占用桌面的采集进程（本程序）
Get-Process QDesktopExtension -ErrorAction SilentlyContinue | Stop-Process -Force
# 2. 重启驱动设备
pnputil /restart-device "ROOT\DISPLAY\0001"
# 3. 等 5~8 秒再看屏数（立刻看会误判成 1 块屏）
[System.Windows.Forms.Screen]::AllScreens.Count
# 4. 再启动本程序，采集源选“扩展屏 · 显示器 N”
```

补充事实：安装只需要 `driver\VirtualDisplayDriver`（inf/cat/dll）和 `driver\tools`（nefcon），
原来的 `VDD.Control*` 备用包与 x64 驱动内容一致，对 x64 没有意义，已从仓库移除。

本机上第二块屏的位置是主屏右侧（`x=2240`，主屏宽 2240），把鼠标一直往右拖就能进到这块屏，
在「设置 → 系统 → 显示」里也能拖动它改位置、单独设分辨率。

卸载/回退：

```powershell
pnputil /delete-driver oem116.inf /uninstall /force
Remove-Item "C:\VirtualDisplayDriver" -Recurse -Force
```


## 实测：手机浏览器收不到 auth_ok，登录框关不掉（已解决）

现象：手机（MIUI 自带浏览器，192.168.31.75）打开 `http://<本机IP>:8767/` 后一直停在「连接远程桌面 / 输入密码」遮罩，
点连接无反应；服务端日志却每次都打印「鉴权通过（模式: JPEG）」，画面（JPEG 帧）也确实在传到手机。

排查到的原因：该浏览器把**连接建立后的控制类文本帧**吞掉了（页面上把最近 6 条消息类型打出来，事件流是 `["B","B","B","B"]`，
只有二进制帧、没有一条字符串），所以 `auth_ok` 从未被解析，`hideLogin()` 也就没执行；而流是服务端主动推的，画面照样在动。

修复（三层兜底，任一层生效即可）：
1. 服务端 `SendAuthOk()` 把 auth_ok 同时用**文本帧**和**二进制帧（首字节 4 = UTF-8 JSON）**各发一遍。
2. 客户端 `onBinary()` 增加两种识别：首字节 `4` → 按 JSON 处理；首字节 `>3`（协议帧头只会是 0~3，JSON 以 `{`=0x7B 开头）→ 也按 UTF-8 文本处理。
3. 客户端以「**收到并画出真实画面帧**」为准进入已连接态（`autoConnected()`，JPEG 与 H.264 两条解码路径都调），
   不再依赖第一条控制消息；同时不再为 stats 重发鉴权（那会导致每秒一次鉴权风暴）。

结论：**判断“是否连上”不要依赖握手消息，以帧为准最稳**——帧能到就说明服务端已鉴权并开始推流。

## 鼠标移不进扩展屏时怎么办

Windows 把多块屏按桌面坐标拼接，只有当两块屏**边缘紧贴**时鼠标才能连续移过去；中间有空隙（常见于主屏 150% 缩放、扩展屏 100% 缩放）
就会出现“往右移光标消失、进不去”的现象。处理办法：系统设置 → 显示 → 拖动 2 号屏，让它和 1 号屏**贴边**（左右上下均可），
再按 `Win+P` 确认选择「扩展」。程序视角（per-monitor v2）里扩展屏位于主屏右侧 `x=2240`，与主屏 2240 宽正好相邻。