# Windows 客户端开发

适用基线：2026-10-22，VERSION 0.12.12。返回 [文档导航](../README.md)。先读 [架构](01-软件架构.md) 和 [协议](02-通信协议.md)，再按本页跟踪一次真实操作。

## 1. 环境和项目结构

解决方案 `SoundConvertClient/UsbAudioBridge.slnx` 包含 Core、Runtime、Engine、App 和 Checks。当前 App／Runtime 使用 `net10.0-windows`，App 为 Avalonia WinExe；依赖版本在 [Directory.Packages.props](../../SoundConvertClient/Directory.Packages.props) 中集中定义，SDK 由 [global.json](../../SoundConvertClient/global.json) 约束。不要在单个项目中随意覆盖统一包版本。

`Directory.Build.props` 从根 VERSION 生成程序集／文件／显示版本；不能只修改窗口文本或 EXE 版本。发布 App 会进一步发布独立 Engine 至 `Engine/`，不能把后台文件遗漏。

## 2. 跟踪“点击连接”

```text
Views/DevicesPage.axaml 中的设备行按钮
  → MainViewModel.ConnectionUi：捕获设备、网卡与发现代次，立即反馈
  → EngineClient：提交 connect 请求、携带 OperationId 和实例身份
  → EngineHost：建立 OperationTicket，派发到 RuntimeDispatcher
  → BridgeRuntime.ConnectTargetAsync：核对网卡、连接和设备 ID
  → Core.ControlSession：TCP HELLO、能力／配置查询
  → BridgeRuntime：更新设备状态并按管理权尝试开始输出
  → EngineSnapshot：返回属性、命令可用性、设备集合与操作状态
  → MainViewModel：在 UI 线程应用快照，更新状态栏和按钮
```

主要入口：[DevicesPage.axaml](../../SoundConvertClient/App/Views/DevicesPage.axaml)、[MainViewModel.ConnectionUi.cs](../../SoundConvertClient/App/MainViewModel.ConnectionUi.cs)、[BridgeRuntime.Connection.cs](../../SoundConvertClient/Runtime/BridgeRuntime.Connection.cs)、[EngineHost.cs](../../SoundConvertClient/Runtime/EngineHost.cs)。

发现目标不仅是 IP。连接应保留设备 ID、原网卡、发现路径及代次，防止 DHCP 后旧 IP 被别的设备占用、用户切换网卡或过期列表误操作。连接／设置第一次点击就显示进度；取消不能被报告为失败，也不能在取消后显示“已连接”。

## 3. 各业务模块到哪里修改

以下路径以 `SoundConvertClient/` 为基准，partial 文件共同构成同一个类，不是额外的服务实例。

| 模块 | 主要文件 | 改动重点 |
| --- | --- | --- |
| 页面、窗口与共享控件 | `App/Views/`、`MainWindow.axaml`、`App/Controls/` | 布局、可访问名称、最小窗口、DPI、中文提示 |
| 前台状态代理 | `App/MainViewModel*.cs` | IBridgeView、属性／命令代理、快照协调、UI 本地反馈 |
| IPC 和调度 | `Runtime/EngineProtocol.cs`、`EngineClient.cs`、`EngineHost.cs`、`RuntimeDispatcher.cs` | 兼容、操作票据、实例变化、有界队列、业务状态串行访问 |
| 连接和身份 | `Runtime/BridgeRuntime.Connection*.cs`、`BridgeRuntime.Identity.cs`、`BridgeRuntime.NetworkAdapters.cs`、`Core/AutoConnectFlow.cs` | 身份验证、选定网卡、管理权、取消与目标保存 |
| UDP 广播／网络配置 | `UdpDiscovery.cs`、`UdpNetworkViewModel.cs`、`IpAddressEditor.cs`、`ConfigurationViewModel.cs` | UDP 广播网卡与取消、事务恢复、challenge 与 revision |
| USB profile／格式／灯效 | `BridgeRuntime.UsbProfile.cs`、`HostFormat.cs`、`Indicator.cs` | 草稿／已保存／当前枚举分离，运行意图恢复 |
| UDP 策略 | `BridgeRuntime.UdpPorts.cs`、`App/MainViewModel.UdpPorts.cs`、`Core/UdpPortSettings.cs` | 四字段原子保存、实际绑定、兼容固件能力 |
| 播放和统计 | `BridgeRuntime.Playback.cs`、`PlaybackMetrics.cs`、`Core/AudioBuffer.cs`、`Network.cs` | 音频线程、样本时间轴、缓冲、恢复和准确统计 |
| 输出设备与 Windows 音量 | `BridgeRuntime.AudioDevices.cs`、`WindowsAudio.cs`、`OutputAudioControl.cs` | 实际端点、系统默认跟随、排除板卡输出 |
| 电脑输入和发送 | `BridgeRuntime.Input.cs`、`InputAudioControl.cs`、`Core/MicrophoneSender.cs` | 显式启动、采集格式、单声道、有界发送和停止 |
| 运行增益／HID | `BridgeRuntime.BoardVolume.cs`、`CaptureVolume.cs`、`HostMedia.cs` | 增益作用域、最新值合并与一次性按键区别 |
| 声音处理／电平 | `BridgeRuntime.AudioProcessing.cs`、`Metering.cs`、`Core/AudioPostProcessor.cs` | 系数／参数更新、缓存读数、DSP 对波形的影响 |
| 时钟观测 | `BridgeRuntime.Clock.cs`、`ClockObservationSession.cs`、`OutputClockObserver.cs`、`Core/*Clock*.cs` | 质量门控、过期、资格退化和 Legacy 回退 |
| 托盘与生命周期 | `App/TrayService.cs`、`WindowsTrayIcon.cs`、`MainWindow.axaml.cs`、`Runtime/EngineLifetime.cs` | 隐藏／退出、Job、进程身份与跨 DPI 菜单定位 |
| BOOTSEL 烧写 | `App/Firmware/`、`Views/FirmwareFlashWindow.*`、`Core/FirmwareUf2.cs` | UF2 校验、目标卷身份、传输互斥、退出保护 |
| 持久化与日志 | `Runtime/LocalStore.cs`、`BridgeRuntime.Shell.cs` | 设置迁移、备份、日志轮转和导出 |

## 4. 给页面增加一个状态字段

以“显示某个板卡运行计数”为例：

1. 先确认它能从 GET_STATUS／GET_STATS 或本地缓存取得，明确单位、当前值／累计值、重置时机和未知状态。
2. 在相应 BridgeRuntime partial 中增加只读属性，在状态读取后触发 PropertyChanged；不要让属性 getter 临时发 TCP 请求。
3. 在 [IBridgeView.cs](../../SoundConvertClient/Runtime/IBridgeView.cs) 增加界面契约，在 App 的 MainViewModel 对应 partial 增加代理属性。
4. EngineHost 的快照会反射采集支持类型的 scalar 属性；自定义复杂类型需显式添加到 EngineSnapshot 与前台应用逻辑，不能假定自动传输。
5. 在 `.axaml` 绑定显示，对无数据、断开、过渡和固件不支持给出明确状态。
6. 用板卡替身验证属性刷新、未知值、旧快照和重连，再在需要时核对实板读数。

前后台类型和属性名是内部协议的一部分，名称拼错常表现为空值或命令不可用。改变不兼容消息契约时同步检查 IPC 版本，保留版本不匹配拒绝逻辑。

## 5. 增加一个可执行操作

先确定操作在哪一层发生：打开窗口／系统设置属于 App；网络管理、音频资源和配置属于 Runtime；封包和算法属于 Core；真实 USB 动作最终属于固件 Core 0。

Runtime 操作应采用现有异步命令及 `ExecuteOperationAsync` 等调度路径，维护忙状态与 CanExecute。App 代理通过 IPC 提交，不在点击回调直接创建 ControlSession 或 WASAPI 设备。EngineHost 将操作变为票据，并以终态反馈真正结果。

必须分别处理：

- 排队尚未执行，可安全取消；
- 已发送、结果可确认；
- 请求被拒绝，可说明原因；
- 超时或连接中断、结果未知，应重新读取状态；
- 后台实例变化，旧操作记录不能当作新实例结果。

播放／暂停、下一曲等一次性动作不能通过重试新 ID 弥补“未知”，否则可能执行两次。连续滑块值则可使用 [LatestValueWriter.cs](../../SoundConvertClient/Runtime/LatestValueWriter.cs) 合并中间值，写前核对会话和目标仍有效。

## 6. 配置开发：草稿、保存、应用

### 电脑本地设置

[LocalStore.cs](../../SoundConvertClient/Runtime/LocalStore.cs) 的 `Settings` record 是持久化字段入口。新增字段需提供缺省值，检查旧 JSON 缺字段、损坏回退和结构迁移；通过 CurrentSettings 等现有保存路径保留其他字段。

保存使用临时文件与替换，产生 `.bak`；旧 schema 迁移保留 `settings.schema1.bak`。不要在页面自己另写一个同名 JSON。预览环境可隔离数据目录，不能让 UI 检查覆盖生产配置。

四项端口是典型例子：前台 `UdpPortDraft` 随保存命令携带完整值；Runtime 一次解析验证并保存 `UdpPortSettings`，成功才替换已提交策略。普通 `set` 与保存异步排列，不保证只发命令就能取得所有最新草稿。保存失败保持原值，当前流继续使用已有描述符。

### 板卡持久配置

USB 名称、类型、格式、HID 和 LD2 通过板端事务保存。流程应捕获原 USB／播放／麦克风意图 → 停止相关资源和模拟 → 核对 STOPPED／revision → VALIDATE／PREPARE／APPLY／CONFIRM → 读回 → 有条件恢复。

不要在 ComboBox 选择时立即写 Flash。未知 profile 或灯效枚举要保留原值并要求明确选择，不能自动变成默认；旧固件未声明新能力时禁用该设置，并在保存其他字段时省略不支持字段。

网络维护还要记录 transaction_id、设备身份、原网卡和待确认状态，以便软件重新打开后继续查询／确认／回滚。不能仅依赖仍存在的搜索列表。

## 7. 音频代码怎么改

### 播放接收与缓冲

`AudioReceiver` 在专用线程接收 UDP，验证来源和流身份，按协商 codec 解码后通知 `AudioBuffer.Add`。保持回调短小和线程安全，不向 Avalonia Dispatcher 按包投递，不枚举设备、不保存设置、不同步等待 TCP。

`AudioBuffer` 按源帧处理重排、缺口、预填充、漂移参考和连续重采样；WASAPI 回调按输出帧请求。改计算时先标清 source frames／output frames／bytes／ms，不能统一除以 48000。

名义重采样步长为 `sourceRate/outputRate`，再叠加有限 ppm 修正。修改抖动估计、水位 PI、滤波器、恢复渐变或修补算法时，分别比较连续性、频响／失真、耗时／分配及实际延迟。不能通过放大缓冲、削峰或不再统计缺口让结果看似变好。

支持格式集合由 [FormatPolicy.cs](../../SoundConvertClient/Core/FormatPolicy.cs) 等决定，UI 过滤和底层重采样能力是不同层。不能因为算法能降采样就绕过产品当前的格式声明策略。

### WASAPI 与系统默认设备

`windows-system-default` 是保存的选择标识，实际端点 ID 是随系统变化解析出的值。按 Multimedia role 解析默认端点，明确选择的端点不随默认改变。设备消失、默认切换、与板卡输出重合时，需要明确报告并按既有流程恢复。

电脑输出音量属于 Windows 端点控制，应用播放增益属于本地流水线，内部音量属于板卡运行控制。禁止用新增 Windows Host setter 混合这些作用域。

### 反向麦克风

WASAPI 采集回调只做有界转换／复制，MicrophoneSender 工作线程完成重采样、封包和发送。队列过多时要按明确时间轴丢弃陈旧数据并记录，不能发送积压的旧录音追赶。停止输入时撤销发送、保活和采集，不能留下采集设备继续占用。

### 声音处理与电平

DSP 包括单声道合并、平衡、三段 EQ 和无前视限幅；参数验证为有限值，系数更新不能引入回调中重型工作。电平显示读取有界缓存，不从 UI 再采集一份音频。恢复渐变和缺口修补需保留真实丢失统计。

## 8. UI、托盘与退出

当前使用编译绑定，页面类型为 IBridgeView。修改共享控件应检查主页、播放页和托盘是否复用；NoWheelComboBox 防止滚轮意外改选，状态按钮文本、颜色和可用性必须使用同一实际状态。

状态栏固定在主窗口外层，不随内容滚动。操作反馈需在首次点击后立即出现，不等一次慢请求回来才置忙；频繁更新不要导致整排媒体按钮闪烁或布局跳动。最小窗口 1050×680 和不同缩放下仍需能滚动访问主操作。

托盘隐藏保留前台和音频；真正退出调用统一清理。不能新增“退出界面但后台继续”的分支，也不能将后台改为脱离 Job 的常驻进程。CLI 诊断路径与 GUI 启动分开，保留终端输出，避免恢复 GUI 控制台闪窗。

## 9. UF2 工具与普通业务分开

烧写窗口无需板卡网络会话。Core 的 UF2 验证模块负责芯片 family、块结构、连续地址和配置区保护；WindowsBootselDrives 负责发现与重新验证卷身份，FirmwareFlashViewModel 维护选择与传输状态。

目标断开后清除选择，不能自动把另一块盘当作原目标；校验后冻结镜像，写入经卷 GUID 打开；打开、写入、flush、close 任一步失败都不能报告完成。软件退出时要检查是否仍在传输。该功能不应通过新增固件 TCP 命令实现。

## 10. 开发一个小功能的完整交付

例如新增一个“只读运行状态”：

1. 记录修改前截图、输入状态和当前版本。
2. 明确数据源和单位，完成 Runtime 属性、IPC／IBridgeView／MainViewModel 与页面绑定。
3. 用离线或替身检查覆盖正常、未知、断线、过渡和重连；只改文字或布局时做实际渲染检查即可。
4. 若涉及板端字段，编译双方并在实板读取验证；不涉及硬件时不要写成“实板已通过”。
5. 对音频敏感修改做相同工况前后比较，发生连续性、性能或体验退化时回退。
6. 更新本指南、用户说明和对应协议，版本和发布产物按 [构建测试与发布](05-构建测试与发布.md) 核对。

目前 `Checks` 和 `tools` 多采用可执行检查程序，不等同于统一的 `dotnet test` 项目。应按具体项目运行，避免只执行 `dotnet test` 就认为覆盖了全部回归。

## 意外断链自动重连

`BridgeRuntime.Reconnect.cs` 独立维护当前会话的重连目标与取消令牌。设置 `AutoReconnectEnabled` 默认 true，旧配置缺少该字段也默认 true，修改立即保存；不改变启动自动连接选项。只在当前进程曾成功连接后记录目标，首次连接失败不进入重试。控制连接的失败检测独立于音频监听意图，因此空闲、停止播放和 USB STOPPED 也能恢复控制。

首次重试等待 1 秒，失败后等待 2、5、10 秒，后续保持 10 秒。每次只在原网卡上尝试旧地址或用 UDP 发现匹配的永久 ID；原网卡变化时等待恢复，不转移到其他网卡。每次恢复都重新握手和读取实际状态；恢复原来的监听/麦克风请求，不自动启动已停止的 USB 模拟，不重放配置事务或一次性控制命令。

显式断开、取消或退出清除本次重连目标；关闭配置立即取消活动重连，仍保留中断目标供用户重新打开开关。存在待处理网络配置事务时停止自动重连，交由显式事务恢复。参见 `docs/开发流程/54-断链自动重连与默认开启设置验收.md`。
