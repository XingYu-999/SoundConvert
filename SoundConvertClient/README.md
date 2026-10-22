# SoundConvertClient

当前功能已精简：删除歌曲名称、歌手、封面、时长／进度、Windows／Android媒体会话读取、配套发送应用及配对服务；删除Windows Host绝对音量设置和网络改写播放Feature Unit的旧接口。保留UAC1音频、音量±／静音／播放暂停／上下曲HID键、独立声卡内部音量和电脑耳机输出控制。内部音量上电80%，0–100%线性幅值，运行时配置，重启恢复默认；USB软断连保留。升级前退出旧前台及后台，使用完整新目录。当前依据见[协议功能精简验收](../docs/24-协议功能精简与内部音量验收.md)。以下旧版记录按其标注版本理解，已删除的歌曲／Host绝对音量功能不再适用于当前版本。

默认开启设置中的“与虚拟声卡意外断链后自动重连”，与启动自动连接分别配置。开关立即保存；原网卡上按设备唯一 ID 恢复，失败后按 2、5、10 秒退避，最长 10 秒并持续重试。手动断开、取消、关闭开关或退出停止重连。

当前软件交付 **V0.12.12**，文件版本 **0.12.12.0**，IPC v4。新增取消搜索、取消连接，失败和后台中断时结束忙碌状态。发现、未连接改 IP 和网络恢复统一使用 UDP 广播；保存前仍校验设备身份、原网卡、boot、revision、challenge 与事务。完整程序位于 `artifacts/publish/v0.12.12-win-x64/`，配套 UF2 位于 `SoundConvertBoard/build/`。

V0.8.0 历史批次：新增设备类型、Windows HID 兼容、电平、DSP、独立麦克风控制、真实 Windows 媒体会话、TLS 配套组件与封面。IPC 升级为2，升级前退出旧前台及后台，完整保留 Engine 子目录。 本轮Windows指定耳机与双向实板范围已验证；手机实机等待后续Wi-Fi ADB，整机挂起与长期测试保持未测。见[实施验收](../docs/18-USB扩展全任务实施与验收.md)与[使用说明](../docs/19-播放端元数据与音频扩展使用说明.md)。

以下旧版段落保留为历史说明（不作为当前功能入口）。

V0.3.0 历史发布，程序文件版本 **0.3.0.0**。新增“输入”页：选择/检测麦克风，向开发板独立发送PCM；默认播放优先，不随软件启动自动采集。录音格式受当前输入源配置上限约束，默认48k/16单声道。首页和监视见 [文档15](../docs/15-双向音频与麦克风实施验收.md)。起播/重缓冲对齐目标加一包以内；新增 PCM 与 IMA ADPCM 选择，默认 PCM，24 位输入在 ADPCM 下先量化为 16 位再有损编码。配套能力未确认时压缩选项置灰。最新实板范围见 [文档 14](../docs/14-起播水位修复与传输压缩验收.md)。修复 192 kHz/100 ms 内部缓冲覆盖，改进时间相关抖动估计、稳定漂移参考、相位插值与 SIMD 重采样。播放页提供低延迟/均衡/高质量，默认均衡；网络纠错默认关闭，需配套新固件。前后同条件耳机实测、离线频响/耗时及验收范围见 [文档 12](../docs/12-音频升级前后指标与实板验收.md)。原有网卡选择、直接连接、自动连接开关、配置事务与托盘流程保留，旧设置默认迁移且保留各字段。

首次按钮点击会立即显示进度，若后台刷新占用设备则等待刷新完成后执行；等待和执行期间禁用重复、冲突操作。正常模拟时可停止，正常连接时可断开；USB 正在启动、停止或处于异常时禁用新的启动，等待 USB 音源时仍可停止监听。

设备搜索、未连接 IP 配置和恢复统一使用 UDP 广播，无需额外驱动。搜索与连接进行中可以取消，失败或后台断开会结束忙碌提示并恢复可重试状态。

此前音频修复采用持久监听订阅：Windows 左右分段测试音或提示音停播后，不再等保活后重新协商。USB 重新送数时直接按新 generation 的实际格式播放；旧代次包仍拒绝。需要配套 V0.0.4 及以上的修复固件，当前统一交付 V0.4.0。

V0.0.3 自动将较低或相同采样率/位深的输入转换到当前输出格式，16-bit/48 kHz 可在 24-bit/96 kHz 输出下播放；仍过滤需要降采样或降低有效位深的输入。9 条实板数字回环通路已逐项验证，范围与启动预热计数见 [验收记录](../docs/09-实施与实机验收记录.md)。

虚拟声卡页UDP 广播搜索后显示固件版本；未连接时选择设备并点击“修改 IP 地址”，弹窗支持 DHCP 或静态 IPv4/前缀/网关，验证完成后自动确认保存。UDP 广播恢复提供查询结果、确认和回滚入口。输出格式现分别显示 Windows 设置的设备位深与 WASAPI 混音格式，例如 N300 为设备输出 24-bit/96 kHz，内部混音 32-bit 浮点。

Avalonia 12.1.1 / .NET 10 Windows 上位机。当前提供 10 种双声道 UAC1 16/24-bit 格式勾选，按当前输出能力自动过滤；接收和播放按 USB Host 实际选择动态协商。工程目录采用实际存在的 `SoundConvertClient`，替代旧指南的 `host` 计划目录。

0.5.0-dev 的主页、虚拟声卡、播放、自动连接、运行中保存与格式过滤详见 [当前功能说明](../docs/10-主页与虚拟声卡配置.md)。下文带有旧九页面、高规格开关与手动停止步骤的截图和验收段落保留为 0.4 及之前版本历史记录。

## 构建与运行

```powershell
Set-Location D:\Github\NS2_Sound_Convert\SoundConvertClient
dotnet restore .\UsbAudioBridge.slnx --locked-mode
dotnet build .\UsbAudioBridge.slnx -c Release --no-restore
dotnet run --project .\Checks\UsbAudioBridge.Checks.csproj -c Release --no-build
dotnet .\App\bin\Release\net10.0-windows\SoundConvertClient.dll
```

SDK 固定 10.0.401；NuGet 集中版本管理与 packages.lock.json。离线检查采用独立控制台，退出码非零表示失败。回归项目覆盖：拆包、半帧 EOF、免配对首次连接/重连、会话序号与 boot 校验、旧固件拒绝、JSON 重复键、科学记数整数、音频格式/溢出、采样时间轴、静音、FIR 幅频，以及 ±100/±300 ppm 的 1200 秒虚拟时钟模型。模型时间不是实际长时间运行。检查数量随版本增加，以本次运行输出、`artifacts/offline-checks.json` 和[实机记录](../docs/09-实施与实机验收记录.md)为准；下文 187/193 项等数字保留对应历史版本的证据范围。

## 桌面功能

- ControllerForwardingTool 风格的侧栏/顶栏工作台，主页快速搜索、记住开发板、一键连接；管理连接成功后中文提示并自动监听，观察连接不接管。
- UDP 分网卡搜索、永久身份与路径显示、手动 IPv4、HELLO 直接连接、管理/只读会话区分。
- USB 模拟启动/停止、状态轮询、运行中编辑与保存；保存时自动 STOP、提交配置、恢复原模拟状态，格式按输出能力过滤。
- 主页与虚拟声卡页提供 USB 媒体键及独立声卡内部音量（上电默认80%）；播放页控制所选输出声卡端点音量。高级本地监听增益独立。
- UDP PROBE/接收/keepalive、有限时间轴、缺帧补零、短淡入、连续 ASRC、WASAPI 共享输出、端点选择、本地音量/静音；输出端点异常停止时终止本地监听与网络流。
- 活跃监听中检测 USB 流终止并重新协商；控制失败后有限次数重新连接。用户停止撤销恢复意图；只读恢复不会抢占原订阅者。
- DHCP/静态网络候选校验、准备、试用、重连后的显式查询/确认/回滚；保存待处理事务和设备身份，未知结果不能显示为成功。
- UDP 广播搜索及网络准备/应用/确认/回滚，校验设备身份、challenge、boot、revision 和事务；不修改电脑地址。
- 托盘显示、菜单控制、关闭隐藏、显式退出；当前用户单实例与具备 CurrentUserOnly 权限的命名管道唤醒。
- 当前用户设置、登录自启、有界日志和脱敏诊断导出。

V0.4.0 将设备连接、播放、麦克风、保活与恢复迁入不依赖 Avalonia 的 Runtime/Engine；App 的 MainViewModel 为命名管道状态代理。构建会复制 Engine 子目录，发布会独立发布该后台。V0.9.2 起由前台生命周期管理器持有 Windows Job：正常退出先请求后台清理并等待进程结束，超时回收；UI 异常结束由系统自动回收后台。后台在初始化前加入前台 Job，拒绝独立启动。收起到托盘保留 UI 进程及当前音频；再次启动由原有单实例入口显示窗口。音量使用最新值合并队列，保留在途响应并避免重复格式查询。协议与验收见 [文档16](../docs/16-独立后台与音量连续性验收.md)。

启动项只修改本产品 HKCU Run 值，保存失败恢复原值。关闭默认隐藏到托盘；真正退出使用托盘或“软件设置”的退出按钮。托盘创建失败时关闭直接退出；首次尚未配置设备仍显示窗口。未配置自动连接时启动不连接；配置后按 IP 优先、永久 ID 搜索回退，失败停止并提示；用户成功建立管理连接后自动监听。不会修改系统默认声音输出，也不默认录音。GUI 初始化脱离控制台，CLI 输出保持原行为。

连接无需配对码或配对文件。搜索后选择设备即可连接并自动监听，设备地址和网卡路径自动填入。仅自动保存常规设置与已连接设备记录；旧配对文件不再读取或使用。固件需同步更新为 0.4.0-dev 及以上直接连接版本；当前局域网内可达的客户端均可建立连接，首个管理会话保留所有权，后续会话只读观察。

## 新界面日常操作

界面已按 `D:\GithubClone\ControllerForwardingTool` 的实际 MIT 源码和用户截图重绘，采用 216/72 DIP 侧栏、64 DIP 顶栏、浅灰工作区与白色卡片。当前七个页面为主页、虚拟声卡、播放、日志、更新日志、设置、关于；原 USB 声卡与网络内容整合在虚拟声卡页；没有大标签 TabControl 主导航。

### 连接并自动聆听

1. 主页“快速搜索开发板”会显示局域网发现路径；选择设备后点击连接，无需配对码或配对文件。
2. 成功连接后保存永久 ID、地址与路径。主页“已记住的开发板”可一键连接；地址改变时重新搜索更新路径。
3. 取得管理权限后，先向选定的电脑输出播放原创中文女声“声卡已连接”，再自动建立监听。主动断开或检测到控制连接失效时播报一次“声卡已断开”；停止监听、音源暂停和应用退出不播报断开。两种提示共用设置开关；自动恢复成功不重复播报连接提示。
4. 只读观察会话不播报接管成功、不抢所有权、不自动监听。开发板自己的 USB 渲染端点会被排除，避免声音回授；没有安全输出时只保持连接并提示选择。
5. USB 已手动停止时保持等待，不暗中执行 USB_EMULATION_START。源停播/alt 0 或超过有效包活跃窗口时显示“监听已建立，等待 USB 主机播放”，累计收到过包不代表现在仍在发声。

### USB Host 音量与本地监听增益

“声音”页首块卡片显示 Host 音量。板端 `usb_control` 提供真实 master dB/静音/控制 revision；若本机 Windows 就是该板 USB Host，服务按序列号与实际 PnP 祖先确认端点，滑块和静音写入该端点，让 Windows 发送真实 UAC 控制。百分比来自这个 USB 端点，不是电脑耳机音量，也不是本地 ASRC 增益。

Host 位于 NS、手机或其他电脑时，只读显示板端 dB/静音并提示在 Host 上调整，不伪造远端百分比或强写远端系统音量。“高级：本地监听增益与诊断”只影响本机播放，默认 100%，不写 USB Host 音量。连接信息或端点失效时禁用 Host 控制；拖动音量后立刻切换静音会合并当前目标值提交，避免丢失前一个操作。

### 开启高规格 24-bit / 96 kHz

1. 在“USB 声卡”页停止模拟，等 STOPPED 后解锁配置。
2. 开启“高规格模式：开放 24-bit / 96 kHz”，点击“校验并保存 USB 配置”。写入字段是 `usb.hi_res_enabled`，仍走版本校验和完整配置事务。
3. 保存后保持停止。点击“开始模拟”或整机重启，使 Host 重新枚举；再在 Host 选择所需格式。
4. “设备已声明格式”来自已提交配置/能力：关闭时仅 16/48，开启时同时有 16/48 与 packed 24/96；“Host 当前选择”来自板端 `format_selected`、alt 和实际 `usb.format`。alt 0 明确未选择，不能用声明列表或 Windows API 请求值替代当前实际格式。

本机曾确认 Intel USB 智音/Intel SST 处理链将 Windows 请求的 96 kHz 转成 USB 实际 48 kHz。仅在真实拓扑/驱动证据匹配时归因于该链路；不代表所有 Intel/USB 声卡如此。若高规格已启用但实际仍为 48 kHz，检查 Host 格式、音效和驱动处理，以开发板实际 alt/格式及网络音频包为准。应用不会自动禁用驱动或更换系统组件；这类维护需独立确认。

### 视觉与交互证据

- [设计 QA](design-qa.md) 和 [MIT 来源署名](THIRD_PARTY_UI_NOTICES.md)。
- [新主页](artifacts/ui-redesign/00-概览.png)、[连接页](artifacts/ui-redesign/01-开发板连接.png)、[声音页](artifacts/ui-redesign/02-声音.png)、[USB 声卡页](artifacts/ui-redesign/03-USB%20声卡.png)。
- [设置页](artifacts/ui-redesign/06-设置.png)、[关于页](artifacts/ui-redesign/07-关于.png)、[折叠导航](artifacts/ui-redesign/10-collapsed.png)、[小窗口](artifacts/ui-redesign/11-small-audio.png)、[停播状态回归](artifacts/ui-redesign/13-idle-after-audio-page.png)。

原生离线渲染共有 14 张，验证九个导航命令、折叠和三项活跃状态断言；明确标记的离线状态图不是实板验收。主流程另已实际验证主页一键连接→自动监听，以及 Host UI 35%→独立 Windows 读回约 35%；完整硬件与格式结果见[文档 09](../docs/09-实施与实机验收记录.md)。

## 实板命令行

以下命令在仓库根目录执行。结果为 JSON Lines；非零退出码表示失败。开发板每次上电或整机重启自动开启 USB 声卡模拟，不依赖认证。手动停止后保持停止，直到显式 START 或整机重启；认证、重连和 Host 插拔不会触发启动。

```powershell
$client = 'SoundConvertClient/App/bin/Release/net10.0-windows/SoundConvertClient.dll'
dotnet $client discover
dotnet $client info --host 192.168.0.22
dotnet $client start --host 192.168.0.22
dotnet $client stop --host 192.168.0.22
dotnet $client stats --host 192.168.0.22
dotnet $client receive --host 192.168.0.22 --seconds 10 --local 192.168.0.14
```

`receive` 执行 PREPARE → UDP PROBE → START → 每秒 KEEPALIVE → STOP。接收端口默认 49162，`--receive-port` 可修改。`--play --output ENDPOINT_ID` 同时播放；没有 `--play` 只接收。报告有效包、非零左右样本、左右峰值；没有有效包返回 4。合法静音可以通过协议接收，但不能证明测试音链路通过。

```powershell
dotnet $client --audio-devices
dotnet $client --usb-test-tone '开发板 USB 声卡完整端点 ID' --seconds 15
# 高规格已开启且 Host 接口支持时，可显式请求 24/96；仍需核对板端实际格式
dotnet $client --usb-test-tone '开发板 USB 声卡完整端点 ID' --source-format 24/96 --seconds 15
```

测试音只发送到指定端点，不改默认：左 440 Hz、振幅 0.08；右 880 Hz、振幅 0.05。当前固件支持先建立监听，再从 USB 主机发送测试音；停播后保留监听订阅，下一段音频无需重新协商。旧版固件仍采用代次失效后重建的兼容路径。

```powershell
dotnet $client --monitor-check --host 192.168.0.22 --output '电脑耳机或扬声器端点 ID' --seconds 10
```

该显式诊断命令接收开发板音频并通过指定普通输出播放，同时用相同端点 WASAPI loopback 测量 RMS、左右峰值、左右 440/880 Hz 分量；仅计算统计，不保存录音。loopback 可能包含其他应用正在播放的声音，需结合预期频率和左右分布判断。它证明软件输出路径，不能代替人耳或模拟线路测量。

默认抖动缓冲上限 **20 ms**，GUI 可配置，CLI 可显式指定 `--max-buffer-ms 40`。WASAPI 请求 10 ms，诊断展示实际初始化缓冲帧/毫秒、API 报告流延迟、实际输入块需求、预填充和上限；每次输出需求超限时对该回调补静音并增加 oversized_read_callbacks，当前需求恢复后清除当前报警；保留历史最大需求与超限总数，不静默扩大。实测这台 Realtek 端点实际后端缓冲为 1056 帧 / 22 ms，首回调可能超过默认 20 ms 上限，后续较小回调能正常播放。实际缓冲查询针对锁定 NAudio 2.2.1 的 `audioClient` 内部实例做只读诊断；不可读取时显示 null，不用请求值冒充实际值。上述数据都不是端到端延迟。

## 配置验收

```powershell
dotnet $client --acceptance --host 192.168.0.22
```

这是显式写入验收：RUNNING 拒绝 USB 修改、STOP 完成、重连保持 STOPPED、名称事务提交后仍停止、显式 START。临时名称 `UAB-Acceptance`，最后恢复原 product_name/playback_name 和初始状态；恢复失败输出 `restoration_failed` 并非零退出。Windows 描述符、Flash 断电保存仍须另外验证。

网络 GUI 中先显式停止 USB（当前固件 Flash 保存要求），试用候选后重新发现相同永久身份并连接，再显式确认。静态 30 秒/DHCP 120 秒未确认由固件回滚；应用或确认应答丢失时使用保存的事务查询，不能创建新的重复事务。USB 名称提交期间也保存待处理事务，发生通信失败后可使用事务查询/确认/回滚恢复。

## UDP 广播恢复

```powershell
dotnet $client --discover-udp --local '电脑网卡IPv4'
dotnet $client --udp-apply --device-id '32位设备 ID' --adapter '发现返回的AdapterName' --mode static --ip 192.168.8.73 --prefix 24 --gateway 192.168.8.1
dotnet $client --udp-confirm --device-id '32位设备 ID' --adapter '发现返回的AdapterName' --transaction-id '准备应答中的32位事务 ID'
# 或 --udp-rollback
```

电脑向选定网卡的 `255.255.255.255:49160` 发送 UABD；板卡向请求中的回复端口广播应答，适用于同一广播域内不同 IP 子网和板卡尚无地址的情况。请求增加非关键 TLV `0x0010`（2 字节，大端回复端口）。客户端分别绑定精确源 IPv4 的发送套接字及 ANY 地址的接收套接字，通过接收接口编号限制回复来源网卡。

保存时仍执行准备、应用、发现验证、确认；静态配置 30 秒、DHCP 120 秒未确认会回滚。APPLY 停止 USB 模拟，保存后需显式开始模拟或重启。请求重试保留相同请求 ID 和内容；结果未知时保留事务供查询，不能重复创建写入事务。广播不能跨路由器或隔离 VLAN；正常连接与音频仍需 IP 可达。必须同时更新配套软件和固件。

## 实现与验证边界

`Core` 不依赖 Windows/UI，承载协议、网络、有限采样时间轴和 DSP；`App` 负责 Avalonia、Windows 输出、托盘、设置与诊断；`Checks` 验证生产 Core 代码。UDP 广播协议与真实套接字替身检查位于 `SoundConvertClient/BroadcastChecks`。

V0.1.0 ASRC 使用 Blackman-window sinc、1024 相位加相邻相位插值，提供 48/96/128 个名义 tap 的低延迟/均衡/高质量档，降采样时按源/输出比例延长滤波器；默认均衡。48→48 kHz 均衡前瞻 1 ms，192→48 kHz 使用 384 taps、前瞻约 1 ms。不可变滤波器在打开监听前后台预热，SIMD 处理连续工作窗口。环形容量 32768 源帧，用户 4～100 ms 逻辑上限独立保留，容量预算包含 FIR 历史、前瞻与最大包。

PI 时钟控制继续限幅 ±500 ppm、变化率 100 ppm/s，按实际输出帧时长更新；启播/重缓冲时固定实际水位参考，与抖动估计出的下一次预填充目标分离。旁路源时钟观察器只报告估计和置信度。播放页 FEC 默认关闭，启用后协商 XOR4，每四个音频包增加一个修复包；实际 UABA/UABF 字节开销约 26%～30%，每组至多恢复一个丢包。具体硬件与离线指标、失败长测和适用范围均见文档 12。

尚未完成：IAudioClient3 周期选择、Windows 睡眠/恢复与端点失效通知的完整专项验证、设置分组迁移/导入、高负载 ETW 与零分配网络路径、干净机器发布安装、24 小时与多主机兼容矩阵。不能把构建/离线/短时板卡结果称为全部指南验收完成。具体硬件证据以根目录本轮验收记录为准。

## 依赖

| 依赖 | 固定版本 | 许可 / 上游 |
| --- | --- | --- |
| Avalonia / Desktop / Fluent | 12.1.1 | MIT，https://github.com/AvaloniaUI/Avalonia |
| NAudio | 2.2.1 | MIT，https://github.com/naudio/NAudio |
| ControllerForwardingTool 界面参考代码 | 本轮源码快照 | MIT，完整署名见 [UI 第三方声明](THIRD_PARTY_UI_NOTICES.md) |

本项目不再加载抓包驱动。正式发布另附完整传递依赖和许可清单。

## 自包含交付

本机已生成 Windows x64 自包含目录：

`D:\Github\NS2_Sound_Convert\SoundConvertClient\artifacts\publish\ui-v0.5-win-x64\SoundConvertClient.exe`

直接运行，不要求目标机安装 .NET SDK 或 .NET Runtime。完整目录约 245 MiB，必须一起复制，不能只拷贝 exe。`artifacts/publish/SHA256SUMS.txt` 为逐文件 SHA-256。所有发现与网络恢复均不依赖额外驱动。

```powershell
dotnet restore .\App\SoundConvertClient.csproj -r win-x64 --locked-mode
dotnet publish .\App\SoundConvertClient.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false -o .\artifacts\publish\ui-v0.5-win-x64
.\artifacts\publish\ui-v0.5-win-x64\SoundConvertClient.exe --help
```

已验证普通解决方案与 win-x64 发布的锁定还原兼容、Release 编译、该自包含 exe 的命令行启动。尚不能代替干净 Windows 安装/升级/卸载验收。


### USB 主机 Feature Unit 控制验收

先在独立进程向**开发板 USB 声卡端点**持续发送测试音，再执行：

```powershell
dotnet $client --usb-control-check --host 192.168.0.22 --usb-endpoint '开发板 USB 声卡完整端点 ID' --seconds 3
```

保持同一个网络接收流，分阶段测量原音量（暂时取消静音）、降低约 20 dB（受实际端点 dB 范围/步长约束）、静音、恢复。`--seconds` 是每阶段采样时长，另有每阶段 600 ms 稳定等待；默认总计约 15 秒。每阶段报告 PCM 左右 RMS/峰值与固件 `usb_control_revision`，不保存音频。finally 恢复原 dB、scalar 和静音状态，并读回验证；不改变系统默认端点。

只有 PCM 响应与固件控制 revision 变化都满足检查才返回 0。revision 缺失或不变化时，即使 PCM 有音量变化也不能据此证明板端 Feature Unit：Windows 软件增益可能造成同样现象，返回 6 并明确标记。此命令须显式选择板端端点，不向普通耳机端点执行。

本轮真实 USB 控制验收曾发现：端点要求降低 20 dB，PCM RMS 比却约为 0.0097366（约 -40 dB），说明同时处理 master 与 L/R 控制导致了重复衰减。旧“只要变小就通过”的检查已移除；现在按实际端点 dB 差计算 `expected_amplitude_ratio = 10^(dB差/20)`，要求测得 RMS 比在预期 ±15% 内，接近量化底限时允许 2 个 S16 LSB 的误差；期望信号低于 8 个 LSB 则判定无法可靠验收。输出独立的 `volume_gain_matches`、预期/实际衰减与容差。v7 固件采用 master-only 控制后，严格实机复测得到幅值比 0.099742988、−20.022352 dB，静音 PCM 全零、恢复幅值比 1.0000185，且端点原音量/静音已恢复。标准 `artifacts/publish/win-x64` 已包含严格验收版，证据见根目录 `artifacts/acceptance/usb-control-v7-strict.jsonl` 和文档 09。




## v8 上电自运行与已验证的 40/80 ms 监听配置

本轮 UI 与指南已同步“开发板上电自运行”策略：每次上电/整机重启自动模拟声卡，认证不再启动；手动 STOP 后保持停止，直到显式 START 或整机重启。v8 实板已验证：没有上位机控制连接时串口即报告 USB RUNNING/mounted=1，Windows 可读到完整声卡描述符。证据为根目录 `artifacts/acceptance/boot-without-controller-v8.log` 与 `artifacts/acceptance/boot-without-controller-v8/usb-cafe-401b.txt`；保存名称后的无控制连接启动枚举另见 `boot-saved-name-no-controller-v8`。

默认 20 ms 保留为可选低延迟上限，不保证当前 Wi-Fi 下持续无欠载。界面提供本机 Wi-Fi 40 ms 与高余量 80 ms 快捷配置；选择后重新开始监听，需保留到下次启动时在“软件设置”保存。40/80 ms 的初始预填充分别至少为 21.5/41.5 ms，而不是只扩大容量仍以 14 ms 启动；实际调度需要更高时取更高值，绝不超出显式上限。`buffer_configuration_insufficient` 表示实际计算的预填充/回调无法装入上限；`capacity_below_conservative_recommendation` 单独表示低于保守建议，不能混同为已经发生欠载。

WASAPI 启动先等待至少三个输出回调完成并检查故障/超时，再开始网络流，避免固定等待时间不足时把冷启动积压误认作网络抖动。独立本机 fixture 也按同一准备过程启动，并在生产 PCM 结束时先停止输出再读最终统计，避免将测试终止后的欠载归因于稳态播放。

离线虚拟时钟新增在 3.85 秒突然出现 12 ms/30 ms 到包空窗的场景，40/80 ms 均保持零欠载、缺帧、溢出和重同步；187 项通过。真实 Realtek **扬声器**（非先前耳机）本机 WASAPI 回环，各运行 10 秒：

| 显式上限 | 初始保守下限 / 最终预填充 | 左/右目标频率能量 | 欠载 / 缺帧 | 边界调整 |
| --- | --- | --- | --- | --- |
| 40 ms | 21.5 / 31.5 ms | 99.71% / 99.69% | 0 / 0 | 1 次、1 帧窗口裁剪；不能写成零重同步 |
| 80 ms | 41.5 / 41.5 ms | 99.58% / 99.26% | 0 / 0 | 0 次、0 帧 |

证据为 `artifacts/realtek-speakers-warmed-40ms-v4.jsonl` 与 `artifacts/realtek-speakers-warmed-80ms-v4.jsonl`。以上是本机生成 PCM → 应用缓冲/ASRC → WASAPI → 同端点回环，不涉及开发板或网络，不能替代 v8 实板网络复测。目标频率能量按相同采集时长归一，完整分析窗外的尾部不计入目标频率能量，避免旧归一方式出现超过 100% 的比例。

在 v8/40–80 ms 验收阶段，v4 代码曾发布到标准 `artifacts/publish/win-x64`，当时回归为 193 项。当前新版 UI、动态格式和恢复逻辑由主流程统一发布；最新版本、检查数量与逐文件 SHA256 以本轮输出和验收记录为准。40/80 ms 都已通过本机严格短测；由于另一轮实际观测到超过 30 ms 的到包空窗，交付时由主流程为本机当前 Wi-Fi 选择并保存 80 ms 高余量配置。代码默认值仍为 20 ms 低延迟选项，不静默改默认，也不声称任意网络都可稳定使用。

### v8 实板网络 → Realtek 扬声器，40 ms / 30 秒严格复测

实际路径为开发板 `192.168.0.22` → 本机 `192.168.0.14` → Realtek 扬声器共享输出与同端点回环。严格结果已通过：

- 30,010 个有效音频包，非法包 0。
- 启动/稳态欠载、缺帧、溢出、重同步、裁剪帧均为 0。
- 左 440 Hz / 右 880 Hz 目标频率能量分别为 99.62% / 99.49%。
- 实际预填充 21.5 ms；WASAPI 实际缓冲 1,056 帧 / 22 ms；最大到包空窗 14.08 ms；结束时漂移估计 8.83 ppm。
- `tone_quality_accepted=true`、`continuity_accepted=true`，整项 `ok=true`。

完整证据：根目录 `artifacts/acceptance/network-playback-40ms-v8.jsonl`。这是已验证的本机 40 ms 配置；不同网络、设备和负载仍需重新测量，不能将 30 秒结果扩写为 24 小时、所有 Wi-Fi 或已测得端到端延迟。
### v8 实板网络，80 ms / 30 秒严格复测与交付选择

同一实际链路的 80 ms 档也通过：30,011 个有效音频包、非法包 0；启动/稳态欠载、缺帧、溢出、重同步和裁剪帧全部为 0；左/右目标频率能量 99.636% / 99.599%。本轮最大到包空窗 **32.85 ms**、估算建议上限 52 ms，实际预填充 **41.5 ms**；WASAPI 仍为 1,056 帧 / **22 ms**。证据为根目录 `artifacts/acceptance/network-playback-80ms-v8.jsonl`。

因此本机当前 Wi-Fi 交付选择 **80 ms 上限**以保留调度余量；40 ms 是也通过过短测的较低延迟选择，而非对后续更大抖动的保证。80 ms 指允许的最大缓冲，不等于每时刻都积压 80 ms：本轮实际预填充是 41.5 ms，结束时水位约 33.06 ms。

上述 21.5/41.5 ms 预填充、22 ms WASAPI 缓冲和 0.5 ms ASRC 前瞻是不同环节的观测/设计值，**不能直接相加当作已测端到端延迟**。目前没有同步参考与模拟输出双通道测量，端到端 P50/P95/P99 仍标为未测量；80 ms 档通常以更高延迟换取更大的调度容限。



## GUI 阻塞与音频线程回归

最终 GUI 验收发现 UDP 接收循环曾继承 Avalonia SynchronizationContext，截图或 UI 工作可能阻塞音频处理；仅 CLI 验收不足以发现该问题。已作最小线程修复：接收和 keepalive 从后台任务启动，网络层及 TCP 完整帧读取的异步等待使用 `ConfigureAwait(false)`，停止/取消也不依赖 UI pump。未改变缓冲、ASRC、配置上限或音频算法。

真实 GUI 的 Packet 订阅仍仅调用内部加锁的 `AudioBuffer.Add`，不在接收回调修改控件或绑定集合；CLI 控制验收的测量订阅同样加锁。界面状态继续由 UI 调度器定时读取。

新增回归以真实回环 TCP/UDP 和一个从不执行 Post 回调的自定义 UI SynchronizationContext 完成握手/建流，在 UI 线程阻塞期间收到 3 包、后台调用 Buffer.Add、完成 keepalive，再从同一个阻塞 UI 线程停止并收到 STREAM_STOP 应答。结果为 UI Post 次数 0、后台回调检查 true、停止完成 true；全部 193 项通过，完整结构化结果保存在 `artifacts/offline-checks.json`。此回归不访问开发板；若受限运行环境禁止回环连接，应在正常本地终端执行，不把 SocketException 10013 当成协议失败。

此处保留最小线程修复阶段的证据。新版界面沿用后台接收/keepalive和线程安全 Buffer.Add，主流程统一发布当前源码并复验；此前 40/80 ms 实板结果仍有其对应版本范围，不能用增大缓冲替代线程修复。


