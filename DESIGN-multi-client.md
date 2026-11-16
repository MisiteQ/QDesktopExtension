# 多客户端独立扩展屏 —— 设计方案（实施中）

> 目标：**每接入一台客户端（手机/平板），系统里就多一块属于它的虚拟显示器**，
> 各自分辨率、各自画面、各自鼠标键盘，互不干扰。
> 硬约束：**不改变电脑现有显示布局**——笔记本内屏、外接显示器的分辨率/坐标/主屏标记一律不动，
> 只对"我们新加的那块"设分辨率和摆位；没有客户端连接时，系统里不多出任何显示器。

## 一、现状（改造前的结构）

```
AppHost
  └─ 1 个 StreamPipeline（采集 1 块屏 → 1 个编码器 → 广播给所有客户端）
       └─ StreamServer（HTTP 8767 / WS 8765）
             └─ _clients：所有已鉴权浏览器连接（共享同一份视频流）
```

后果：第二台设备接入后看到的是同一画面，面板里也不会多出显示器。

此外，原来的虚拟显示驱动 **MttVDD 已实测"设备初始化后禁止再改分辨率"**
（`ChangeDisplaySettingsEx` 对其枚举出来的模式统一返回 `-1/-2`），
无法支撑"按客户端分辨率原生输出"。

## 二、新依赖：Parsec VDD（已验证可用）

- 驱动：`ROOT\DISPLAY\0002` / `Parsec Virtual Display Adapter`（v0.45）
- 控制协议：官方 `core/parsec-vdd.h` 的 IOCTL
  - `ADD`(0x0022e004) 插一块显示器 → 返回索引（实测返回 0，系统多出 `\\.\DISPLAY155`）
  - `REMOVE`(0x0022a008) 拔掉
  - `UPDATE`(0x0022a00c) 保活，**必须 <100ms 一次，否则 1 秒后自动拔掉**
  - `VERSION`(0x0022e010) 返回 45
- 代码：`ParsecVdd.cs`（已实现并编译通过：`Open/Version/AddDisplay/RemoveDisplay` + 80ms 保活线程）

## 三、目标结构

```
AppHost
  ├─ ParsecVdd（驱动句柄 + 保活线程）
  └─ Dictionary<clientId, ClientSession>
        ClientSession = { 虚拟屏 index, 显示器 deviceName, StreamPipeline }
                                                          ├─ DisplayCapture（只采集这块屏）
                                                          ├─ VideoEncoder（按该客户端分辨率）
                                                          └─ 只向该客户端推流
```

## 四、改动清单（按依赖顺序）

### 1) StreamServer：客户端通道化
- `ClientState` 增加 `Channel`（id、conn、视频参数 codec/desc/w/h、lastKeyPacket、mseInit、keyframe 请求、所属显示器矩形）
- `_videoW/_videoH/_lastKeyPacket/_mseInit/_keyFrameRequested` 这些**全局状态**改为挂在通道上
- `BroadcastFrame / UpdateVideoInfo / RequestKeyFrame / ConsumeKeyFrameRequest / SetMseInit / MseClients`
  全部带上通道参数，只作用于一个客户端
- 新增事件：`ClientJoined(channel)`、`ClientLeft(channel)`、`ClientViewport(channel, w, h)`
- `viewport` 消息不再打到 `ViewportRequest` 全局，而是走 `ClientViewport`
- 输入消息按通道所属显示器的矩形换算坐标（每块屏各自一套映射）

### 2) StreamPipeline：绑定到单个通道
- 构造参数增加 `IStreamSink sink`（默认实现 `ServerChannelSink` 包一层 StreamServer）
- 所有 `_server.BroadcastFrame(...)` / `_server.RequestKeyFrame()` / `_server.UpdateVideoInfo(...)`
  改成走 `_sink`，从而一条管线只服务一个客户端
- JPEG 通道保留全局兜底（不支持 WebCodecs 的客户端仍能看，但不参与多屏）

### 3) AppHost：会话管理
- 客户端鉴权成功 →
  1. `vdd.AddDisplay()` 得到 index
  2. 等新显示器出现在 `Screen.AllScreens`（最多 3 秒轮询）
  3. `ScreenArranger.PlaceRightOfDesktop(deviceName)` 摆到现有桌面最右侧（只动这一块）
  4. 按客户端上报的分辨率设模式（`ChangeDisplaySettingsEx`，Parsec 支持动态模式）
  5. 建一条 `StreamPipeline` 采集这块屏
- 客户端断开 → 停管线 → `vdd.RemoveDisplay(index)`
- 保活：ParsecVdd 内部线程负责；AppHost 退出时 `REMOVE` 全部并 `Dispose`

### 4) MainForm：面板显示所有屏
- "显示模式"页的显示器下拉刷新为"物理屏 + 当前虚拟屏列表"，标注哪块正被哪个客户端占用
- 新增"摆开所有虚拟屏"按钮（依次调用 `ScreenArranger.PlaceRightOfDesktop`）

## 五、非干扰保证（硬要求）

| 风险 | 处理方式 |
|---|---|
| 改动已有显示器分辨率 | 只对 `ParsecVdd` 新建的那块调用 `ChangeDisplaySettingsEx`，按 deviceName 精确限定 |
| 改动已有显示器位置 | `ScreenArranger` 只读其它屏的 `Bounds` 算最右边界，不改它们 |
| 主屏被切走 | 不调用任何 `CDS_SET_PRIMARY`，虚拟屏默认不是主屏 |
| 没有客户端时多出空屏 | 断开即 `REMOVE`；进程退出前统一清理；驱动不保活则 1 秒自动拔掉 |
| 与 MttVDD 冲突（两块虚拟屏并存） | 改造完成后停用 `ROOT\DISPLAY\0001`，只保留 Parsec VDD |

## 六、验证清单

1. 单机（只有笔记本内屏）：连一台手机 → 系统多一块虚拟屏，内屏分辨率/坐标不变；断开 → 屏消失
2. 双设备：连两台手机 → 系统里两块虚拟屏，A 手机看 A 屏、B 手机看 B 屏，互不串台
3. 各设不同分辨率（如 2400×1080 与 2540×1386）→ 两条管线各自编码，画面都是原生分辨率、无拉伸
4. 有外接显示器时：外接屏的分辨率/位置保持不变，新虚拟屏排在它右侧
5. 输入：在 A 手机的屏幕上点，鼠标只作用于 A 那块屏
6. 断开断开再连 10 次：无残留显示器、无内存/句柄泄漏

## 七、实施顺序（每步都保持可编译、可回滚）

1. `ParsecVdd.cs`（已完成）、`ScreenArranger.cs`（已完成）
2. `StreamServer` 通道化（最大的一步）
3. `StreamPipeline` 绑定通道
4. `AppHost` 会话生命周期
5. 面板 UI + 真机验证
