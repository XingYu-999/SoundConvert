# USB 扩展全任务实施与验收（V0.8.0）

> 历史验收记录：当前版本已按用户要求删除歌曲信息、媒体会话及配套服务，Host绝对音量设置也已移除。本文原始版本、失败记录与测量不变；其中安装／配对／权限／Host设置步骤不再适用于当前版本。现行功能与验收见[文档24](24-协议功能精简与内部音量验收.md)。

> 最新结果见末尾“V0.8.0 最终验收与交付结论”。暂停前的缺口和失败为当时记录，后续通过项在末尾覆盖状态，保留原始事实。

实施窗口：2026-10-20 至 2026-10-21。依据 `ToDo/USB媒体控制与声卡功能扩展开发计划.md`，用户授权完成全部开发。V0.6.0、V0.7.0 为本轮中间实施版本，最终统一版本为根目录 `VERSION` 的 V0.8.0；同一批次修复不再递增。

## 实现范围

| 任务 | 实现与结果 | 主要文件 |
| --- | --- | --- |
| T00 基线 | 保存原始源码／产物哈希、硬件、完整 2 MiB Flash、8 KiB 配置、设备状态、USB 描述符、后台快照；保留原六键确认 | `artifacts/acceptance/usb-expansion-baseline`、文档17 |
| T01 配置 | 四种预设；Flash v1—v6 迁移至显式编码 v7/795 字节；新增类型与 Windows HID 兼容位；省略字段保持原值 | `uab_config_store.*`、`usb_device_profile.h`、`uab_network.c` |
| T02 描述符 | 不可变模板→完整 staging→校验→STOPPED 提交；耳机移除麦克风实体／AS；耳麦物理终端关联；序列号保持 | `usb_descriptors.*`、`usb_descriptor_profile_builder.h` |
| T03 配置流程 | 保存／活动／草稿状态、能力降级、录音草稿保留、统一事务及不确定结果恢复、模拟／监听／麦克风意图恢复 | `BridgeRuntime.UsbProfile.cs`、`ConfigurationViewModel.cs`、`UsbPage.axaml` |
| T04 电平 | 有效源帧 L/R 峰值、RMS、满量程计数；50 ms 窗口、衰减、500 ms 无数据；不增加板端轮询 | `AudioLevelMeter.cs`、`AudioBuffer.cs`、`AudioLevelPanel.axaml` |
| T05 麦克风 | 独立 FU 7；S16/packed S24、单位增益快路、静音、约 1 ms 渐变、短写仅推进实际样本；Host／网络统一状态 | `usb_microphone.*`、`usb_audio.c`、`BridgeRuntime.CaptureVolume.cs` |
| T06 元数据 | 来源／板卡／USB 序列号／会话显式绑定；单调序列；暂停、过期、退出、解绑；基础值和会话列表走 IPC v2 | `Media.Contracts`、`MediaSessionCoordinator.cs`、`BridgeRuntime.Media.cs` |
| T07 Windows | 独立 Windows SDK 投影适配项目；事件合并、会话切换取消旧读取、心跳、真实媒体 API；异机 Windows 发送组件 | `Media.Windows`、`Companion.Windows` |
| T08 Android | Kotlin 应用、通知访问、选会话、可见服务、Keystore 加密凭据、固定证书 TLS/WSS、重连／撤销；默认不监听 | `SoundConvertCompanion/Android`、`CompanionMetadataServer.cs` |
| 封面 | Windows／Android 缩略图；独立认证 HTTPS 资源，最大 512×512 / 256 KiB / 1 次每秒；32 项缓存；IPC 仅传路径 | `ThumbnailCodec.cs`、`ArtworkEncoder.kt`、`ArtworkCache.cs` |
| T09 声道／DSP | 共用输出链覆盖浮点与 packed；平衡只衰减、单声道平均、三段 EQ、无前视峰值限幅、约 5 ms 参数过渡 | `AudioOutputPipeline.cs`、`AudioPostProcessor.cs` |
| T10 输出策略 | 应用内部增益上限、恢复渐变、端点消失停止；不抢写系统默认端点 | `OutputGainPolicy.cs`、`BridgeRuntime.AudioProcessing.cs` |
| T11 状态通知 | 独立可撤销原型：0x84、UAC1 两字节、实体合并、GET_STAT 确认、500 ms 限频、每实体20次上限；正式默认关闭 | `usb_control_notify.*`、CMake `UAB_USB_CONTROL_NOTIFY` |
| T12 发布／回归 | 配套构建、固件宿主、生产 ELF、UI、真实 IPC、协议安全及真实 Windows API 分别记录；硬件结果按窗口记录 | `tools/Build-All.ps1`、各 acceptance 日志 |

## 配置与兼容性

预设：`desktop_speaker`（桌面音箱＋独立麦克风）、`speaker`（通用音箱＋独立麦克风）、`headphones`（仅播放）、`headset`（关联双向终端）。耳机模式保留已保存录音格式；`capture_supported=false`，`CAPTURE_PREPARE` 拒绝。

新增 `usb.hid_consumer_compat`：关闭时依预设使用 Consumer Control、Headphone 或 Telephony Headset 顶层集合；开启时统一使用 Consumer Control，供 Windows 兼容。此开关独立、显式保存，不根据探测结果静默更改。原生耳麦集合在本机 Windows 上曾出现六键不响应，而原 Consumer Control 集合可响应，因此保留两种策略。USB 传输完成不能代替 Host 反应。

配置尾部从 v5/793 字节追加类型得到 v6/794，再追加兼容位得到 v7/795。启动读取旧版只在 RAM 迁移；不自动擦写。新保存写 v7，旧固件不保证读取 v7。降级前恢复完整配置备份。记录格式版本与发布版本分别管理。

## 锁定的实机范围

- 板卡：W5500-EVB-Pico2 / RP2350；DAP 校验识别 W25Q16JV，2 MiB；USB Full-Speed / UAC1。
- 网络：板卡 `192.168.0.22`，本机所选以太网 `192.168.0.23`；设备 ID `aa36483cef4256f169f4f4e5322c6df8`。
- USB：序列号 `E53B126BB2E31A4A`，本机 Windows 11 专业版 10.0.26200；用户明确改为 Windows USB Host。
- 串口：COM8，115200、8N1。已捕获真实 `UAB firmware V0.8.0` 启动；见 `v0.8.0/serial-flash-default.txt`。
- 接收输出：`扬声器 (Realtek(R) Audio)`，端点 `{0.0.0.00000000}.{4de9d92f-95af-4e99-9d5f-775750773f1e}`。播放测试暂限15%，每窗口恢复当时读取的 dB／静音。不能沿用历史耳机结论。
- 原管理后台 PID 45892，V0.5.0 / IPC v1；已通过正常断开命令释放会话，原快照保存在 `v0.8.0/owner`。

## 已取得的硬件证据

| 窗口 | 结果 | 原始位置 |
| --- | --- | --- |
| 正式固件阶段构建烧录 | DAP `Verified OK`；COM8 启动 V0.8.0，网络实读同版本 | `v0.8.0/flash-default.log`、`flashed-status-ready.json` |
| 四预设真实 USB 描述符 | 四种均通过；耳机3接口且无麦克风，其他4接口；耳麦类型0x0402及互关联；独立 FU7 | `windows-physical-1/*-enumeration` |
| 四预设连续播放 | 各12秒，板端过期／队列／欠载／重同步／网络丢弃及 PC 缺帧／欠载／重同步／超期／溢出增量均0 | `windows-physical-1/*-playback/playback-window.json` |
| 16位 USB 麦克风实际幅值 | 独占采集：−6 dB 比例0.500768、−12 dB 0.250811、网络−9 dB 0.354446、静音0 | `mic16-exclusive-3` |
| 24位 USB 麦克风实际幅值 | 独占 packed PCM：−6 dB 0.501186、−12 dB 0.251187、网络−9 dB 0.354830、静音0 | `mic24-exclusive-3` |
| 桌面／通用音箱 Windows 媒体 | 受控真实 MediaPlayer：暂停、恢复、上下曲、标题／歌手／状态；三种系统音量键均有端点变化 | `windows-compat-final/{desktop_speaker,speaker}-*` |
| 耳机 Windows 音量键 | 实读 Windows HID caps 为 Consumer page 0x0c / usage1，IN3字节（含系统报告ID）；三键均有实际端点变化 | `headphone-diagnosis/hid`、`volume` |

以上是对应阶段二进制和指定窗口的证据，不能推广为最终所有变更、所有 Host、所有格式组合或长期稳定性全部通过。

## 失败候选与当前缺口

1. 首次烧录后网络尚未就绪，首次查询超时；失败 JSON 保留，后续 `flashed-status-ready.json` 成功。验收脚本增加只读网络就绪检查。
2. 第一轮恢复输入误用了失败查询文件，恢复命令拒绝；后续脚本在写入前检查完整基线及板卡身份，并把恢复放入 `finally`。后续多轮原 USB 配置恢复已成功。
3. 初次麦克风网络增益阶段沿用了 Windows 静音，录得0；保留失败。独占采集排除共享输入处理，清除 Host 静音后，16/24位实际比例通过。
4. 独占采集最初含自动格式转换标志，Windows 拒绝；改为独占正确标志。24位单声道 WAVEFORMATEXTENSIBLE 通道掩码被拒，实读驱动支持 packed PCM，使用其明确支持的格式通过。未改系统默认录音格式。
5. 耳机模式媒体键存在播放器状态未响应窗口；传输完成而实际播放器未变化的候选保留。后续诊断一轮在测试开始前因 USB 渲染端点消失中止，不能把它记成媒体通过。
6. 2026-10-21 约00:23后，本机开发板 VID/PID 不再枚举，独立 COM8 也消失；网络仍在线、USB `RUNNING / mounted=false`，Windows 报告未知 USB 描述符请求失败。已询问用户连接状态。此后不再进行实板操作。
7. 用户随后明确选择“暂不进行实板测试，先完成软件交付”。最终软件包继续交付，实板复验及运行状态恢复等待后续授权窗口。按预设动态更改 bcdDevice 的诊断试验未证明能解决问题，最终不保留；正式描述符固定修订0x0600，独立通知原型0x0700，不改变序列号。

**尚未完成的实机项：** 最终候选固件重新烧录与四预设媒体复验、当前窗口的播放／麦克风并行、物理拔插／挂起恢复、最终配置区逐字节恢复核对、原后台连接恢复；Android 权限／锁屏／Wi-Fi／播放器兼容性；UAC 状态通知实际 Host 收取、GET_STAT、端点与系统界面行为。连接恢复前这些保持“未完成／未验证”。

## 软件验证与交付边界

固件宿主回归包含 HID 队列／释放／代次、USB owner、麦克风真实增益路径及短写、配置迁移／CRC／双槽／掉电、生产描述符过滤和状态通知限频。相关日志在 `artifacts/acceptance`。

本轮已取得：扩展 Avalonia 197项、独立进程 IPC 424项、元数据 TLS/WSS／封面安全30项；真实静音 Windows MediaPlayer/SMTC API 的标题、歌手、暂停、进度、封面和退出失效6项；Android APK 构建及 lint 通过。后续最终构建与复跑结果以发布清单为准，不覆盖旧失败文件。

元数据与封面的协议、部署及撤销见文档19。限幅是无前视峰值限制，可能改变波形；`LatencyFrames=0` 仅表示不引入额外采样缓冲，不能表述为整条声音链路零延迟。电平是板端增益后的有效接收源帧，不是声压，也不是播放器状态。

UAC 通知原型不进入默认描述符；无指定 Host 实际收益和连续性证据时保持关闭。NS2 等封闭主机没有纳入歌曲信息承诺；没有可验证的播放端接口时维持未知。


## 2026-10-21 恢复执行进度

用户已明确恢复 Windows 实板验收，当前网络为板卡192.168.8.73、接收网卡192.168.8.74。接收输出固定为耳机 (Realtek(R) Audio)，ID `{0.0.0.00000000}.{32f84dcf-80da-42e0-86c8-ec92564764aa}`；源端 USB 声卡与电脑耳机分别验证，不修改系统默认输出。

新证据目录：`artifacts/acceptance/v0.8.0/resumed-20261021`。完整Flash及配置区已另行备份；最终正式ELF DAP校验通过，COM8捕获V0.8.0启动。本轮初始逻辑配置通过各工具的finally恢复，不沿用上轮不同网络下的临时状态。

- 构建／发布锁文件问题已解决：普通锁与`packages.win-x64.lock.json`分离；生成发布锁时显式传`-p:RuntimeIdentifier=win-x64`。普通锁定还原→构建→两套Windows自包含发布→再次普通锁定还原通过。对比34项缓存依赖资料及锁文件版本审计，已有包版本无漂移。更新锁文件入口为`tools/Update-LockFiles.ps1`；常规构建不解锁。
- 最新Core离线回归866项、界面202项、实际发布前后台启动9项通过；独立IPC424项、元数据协议30项、真实Windows媒体API6项的原始证据分别保留。发布启动检查使用真实发布目录、真实耳机、隔离数据，不启动板卡连接或音频播放。
- DSP基准已重新生成，显式记录`DOTNET_TieredCompilation=0`。六个48/96/192kHz窗口稳态分配0、新增采样缓冲0帧；这是计算墙钟耗时，不是整进程CPU或物理链路零延迟。分层编译启用时曾记录128字节差异，保留测量条件说明。
- `windows-media-final`：四种预设使用显式Consumer Control兼容位，真实受控MediaPlayer的暂停／恢复／上下曲及三种音量键全部通过。耳机／耳麦旧失败候选不删除，不能把本轮指定播放器结果推广到所有播放器或Android。
- `audio-preserved-formats`：保留原播放格式集合、使用与发布包同字节Core／Runtime，四种预设各12秒耳机播放通过，命名丢帧／欠载／重同步计数增量0；16位独立麦克风增益通过。
- 24位单格式捕获在本机驱动中拒绝所有探测容器，`capture24-cold-ready/formats`保留独占／共享格式探测和冷启动证据。快速格式切换后也出现过Windows音频端点丢失；3秒软断连曾恢复录音端点，完整恢复及物理拔插正在验证，不能宣称问题已解决。
- 初次冷启动验收只等5秒，因DHCP／TCP尚未就绪而失败；验收工具现先执行有界只读TCP就绪探测，再建立控制会话，并核对boot_id变化。
- Android APK版本V0.8.0、versionCode8000、API26起、目标37，开发签名校验通过；lint为0错误1条依赖更新提示。手机端仍按用户安排等待Wi-Fi ADB，入口和清单见文档20。
- 本轮19个一次性变换脚本已逐个归档后移出tmp，只处理确认归属本轮的精确路径；其余文件保留。

当前仍在收尾：24位／双向与端点恢复窗口、通知原型Host结果、配置区逐字节恢复核对、最终软件清单与ZIP验证。后续完成情况以本文追加和最终software-delivery.json为准。


## V0.8.0 最终验收与交付结论

### 最终结果

- 最终正式固件已烧录／DAP校验，COM8记录V0.8.0；USB实际读取为Full-Speed、固定描述符修订0x0600、四接口、FU7、播放01／反馈81／录音82／HID83，正式无84。
- 四预设的真实受控Windows播放器暂停、恢复、上下曲及三种音量键在`windows-media-delivered`全部通过，Windows媒体窗口明确启用Consumer Control兼容位。原生Android集合不由此推定支持。
- 四预设各12秒指定Realtek耳机播放窗口通过；使用发布包同字节Core／Runtime，命名板端及PC计数增量均0。
- 16／24位麦克风各0、−6、−12dB、静音、网络−9dB五步通过。24位实际USB格式逐步核对为24位，−6／−12／−9dB幅值比例约0.501206／0.251219／0.354822，静音0。Windows需要packed24 WAVEFORMATEXTENSIBLE、单声道前中置掩码4；早期探测遗漏该组合，错误归因为驱动不支持，失败记录保留。扩展采集工具已修正，固件无需修改USB修订号或复用策略来解决此项。
- 双向60秒首次发现控制处理最大约39.7ms，USB源过期912帧、PC缺帧960；新增`uab_json_print.h`，逐字段调用原cJSON标量编码，在Core1字段边界按500µs门限服务双向音频。JSON数值／转义／顺序不变，USB回调不参与网络。30755项数值／序列化检查通过。
- 同条件60.65秒双向复验通过：板端USB过期／队列／欠载／重同步／发送超时和PC缺帧／欠载／重同步／溢出均0；麦克风缺帧／队列丢弃／欠载／补零均0。另41.4秒关闭／重开Host录音窗口通过，播放计数仍0。完整控制耗时不是最长音频服务间隔，不能只凭总耗时推断阻塞。
- 用户完成5秒物理USB拔插，Windows播放和录音端点均重新出现。完整TinyUSB回收、临时0x0601修订等诊断候选未解决采集格式问题，均已撤回；最终保留原USB生命周期和固定修订。
- 通知原型真实Windows观察：marked=1、scheduled=1、completed=0、acknowledged=0；板端改至−6dB，而Host端点API读回未变化。12秒原型耳机播放窗口通过；系统设置界面未观察。未证明Host实际收取／确认或界面收益，正式保持OFF，研究二进制单独标识。
- 最初V0.5.0与本次开始时逻辑USB／网络配置相同，已恢复最初备份8KiB原始字节，启动后读回SHA256一致，配置revision恢复53。固件为V0.8.0；旧记录在RAM迁移，新保存才写v7，不自动擦写。两份完整Flash及配置备份保留在源码验收目录，不收入发行包。

### 交付与仍未测范围

发布目录`artifacts/releases/V0.8.0`，软件ZIP、SHA256及解包启动验证报告位于其父目录。Windows自包含客户端／独立后台、异机Windows组件、正式ELF／UF2、Android开发签名APK、默认关闭的通知原型、文档和依赖说明一起交付。每个产物与证据路径以`manifest.json`、`validation/software-delivery.json`为准，解包后的长度／SHA256及真实后台启动结果另存软件包验证报告。

本轮恢复开始时未运行旧前后台，不恢复历史PID；本机已保存输出保持指定Realtek耳机，测试窗口逐次恢复原音量与静音。尚未进行Windows整机睡眠／选择性挂起及唤醒、长时间稳定性和其他Host全面矩阵；不以物理拔插或短窗口替代。Android手机安装、通知授权、锁屏／后台、QQ音乐及Wi-Fi切换按用户安排等待后续Wi-Fi ADB。原型其他Host／系统UI研究继续独立保留。
