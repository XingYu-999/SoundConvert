# SoundConvertBoard 固件

V0.12.2 新增 LD2 指示灯配置，默认 USB 已枚举空闲呼吸、双向音频传输时闪烁，另有常灭、常亮、持续呼吸及持续闪烁。Flash 配置 v8 兼容读取 v1–v7，旧记录默认自动。Core 0 独占 GPIO25 硬件 PWM，Core 1 通过原子请求设置模式及临时识别。配置保存沿用停止模拟后的确认事务。固件已编译及宿主检查，尚未烧写本版本或进行灯效实板验收，见[验收记录](../docs/46-LD2指示灯配置与验收.md)。下文旧版记录按标注版本理解。

当前功能已精简：删除歌曲名称、歌手、封面、时长／进度、Windows／Android媒体会话读取、配套发送应用及配对服务；删除Windows Host绝对音量设置和网络改写播放Feature Unit的旧接口。保留UAC1音频、音量±／静音／播放暂停／上下曲HID键、独立声卡内部音量和电脑耳机输出控制。内部音量上电80%，0–100%线性幅值，运行时配置，重启恢复默认；USB软断连保留。升级前退出旧前台及后台，使用完整新目录。当前依据见[协议功能精简验收](../docs/24-协议功能精简与内部音量验收.md)。以下旧版记录按其标注版本理解，已删除的歌曲／Host绝对音量功能不再适用于当前版本。

当前构建固件 **V0.9.4**，统一读取根目录 VERSION。网络名称使用 `W5500-EVB-Pico2`，DHCP 携带主机名，空闲 Socket 7 提供局域网 NetBIOS 查询应答。已完成编译及宿主回归，未烧写或进行本版本实板验收。见[文档23](../docs/23-网络名称与接收地址显示验收.md)。

V0.8.0 历史批次：四预设采用同一描述符 staging 校验路径，新增独立麦克风 FU7 与配置记录 v7/795字节；保持 UAC1。状态通知端点仅实验构建启用，正式默认关闭。 本轮Windows指定耳机与双向实板范围已验证；手机实机等待后续Wi-Fi ADB，整机挂起与长期测试保持未测。见[实施验收](../docs/18-USB扩展全任务实施与验收.md)与[使用说明](../docs/19-播放端元数据与音频扩展使用说明.md)。

以下旧版段落保留为历史说明（不作为当前功能入口）。

V0.3.0 历史固件，根目录 VERSION 为版本来源。UAC1增加独立单声道IN端点0x82、UDP49163和麦克风FIFO/流控；默认录音48k/16，播放保存默认四种48/96k×16/24-bit。控制JSON期间优先穿插播放服务，详细范围见[文档15](../docs/15-双向音频与麦克风实施验收.md)。新增可协商 IMA ADPCM 实时编码，PCM 默认兼容；每包独立状态，支持压缩后的 XOR-4 修复。USB FIFO 容量与 6 ms 反馈目标保留；不因压缩而取消异步接收缓冲。最新实板与精度边界见 [文档 14](../docs/14-起播水位修复与传输压缩验收.md)。本轮改善 USB FIFO 余量、增加只读反馈与队列诊断、提供稳定增益快路，SPI 实际为 25 MHz，音频发送按 SENDOK 维护完成状态；可协商 XOR-4 修复包，默认关闭。已完成 DAP-link 写入/校验，配置区保留。前后指标及实板边界见 [文档 12](../docs/12-音频升级前后指标与实板验收.md)。固定 TinyUSB 提交需带 vendor 中的只读诊断补丁，Build-All 会校验并按需应用，不重置上游工作区。后文 0.x.0-dev 为历史记录。

本工程面向 W5500-EVB-Pico2（RP2350、实测 W25Q16JV 2 MiB Flash）。
Core 0 拥有 TinyUSB，Core 1 独占 W5500；USB 回调不调用网络驱动。

## 构建与烧写

使用 Pico SDK 2.3.1、Arm GNU 15.2.Rel1、CMake 与 Ninja。SDK 自带的旧
TinyUSB 没有本项目使用的 UAC1 API，须使用 `vendor/DEPENDENCIES.md` 中
锁定的 TinyUSB 提交。当前计算机已有 `D:/GithubClone/tinyusb`。

1. 0.4.0-dev 起不需要配对码、配对文件或本地密钥头文件，直接配置和构建：

   ```powershell
   cmake -S SoundConvertBoard -B SoundConvertBoard/build -G Ninja -DPICO_TINYUSB_PATH=D:/GithubClone/tinyusb
   cmake --build SoundConvertBoard/build -j 8
   ```

2. 使用仓库 `tools/Flash-Board.ps1` 通过 DAPLink/SWD 写入 ELF，并读取
   COM8、115200、8N1 日志。原始示例镜像由主流程保存于 `artifacts/baseline`。

链接区限制为 2 MiB 减去最后 8 KiB。最后两个独立 4 KiB 槽保存配置，记录
包含 CRC、版本和独立提交页；写失败保留另一个旧槽。普通固件更新只写镜像
覆盖范围，整片擦除会删除配置。设备 ID 由板载唯一标识派生，不依赖本地配对材料，也不保存在用户配置槽内。

## 当前实现

0.5.0-dev 提供 UAC1 双声道 16/24-bit 的 10 种格式、运行时格式子集和网络 USB Feature Unit 主音量控制；协议与 Flash v3 迁移详见 [当前改版说明](../docs/10-主页与虚拟声卡配置.md)。以下旧双档与描述符尺寸为历史说明，以该文档及实际能力应答为准。

- W5500 硬件 TCP/UDP；默认 DHCP，无租约时不回退到随机/历史静态地址。
- DHCP 初次获取、同址续租、T1/T2、NAK、租约到期；DHCP 与静态候选均先
  执行 ARP Probe，再开放 IP 业务；检测到冲突撤销地址。
- UABD UDP 双向广播发现和配置（含无地址或同广播域不同 IP 子网）；免配对网络事务、短期
  challenge、重试缓存、确认窗口和回滚。无 IP 时仍保留UDP 广播恢复。
- UABC 两个 TCP 槽；HELLO 直接建立会话、序号与 boot 校验、有限拼帧、
  JSON 重复键/UTF-8/嵌入 NUL 拒绝。副作用请求缓存 32 条，每条 60 秒。
- 上电自运行：Core 0 先加载已提交 USB 名称，再立即开始模拟，随后才启动网络。
  不等待网线、DHCP 或认证；本次运行中手动停止后的重连不自动开始，整机重启
  会再次自动开始。USB 名称与网络配置分开事务，均要求停止模拟后写入。
- USB 名称与 `formats_mask` 事务持久化；支持 16-bit 44.1/48/88.2/96/176.4/192 kHz，
  packed 24-bit 44.1/48/88.2/96 kHz，均为双声道。Flash v3 迁移 v1/v2 时保留名称、网络和 revision。
- 上位机可用 USB_SET_FORMATS 收窄已保存格式的运行时子集，不写 Flash；停止后更新描述符再重新枚举。
- UAC1 Feature Unit 仅主音量/静音：channel 0，−60～0 dB、完整 Q8.8（1/256 dB）步长；L/R
  控制位图为零并拒绝请求。主增益对双声道 PCM 各应用一次，避免 Windows
  同时设置 master/L/R 时重复衰减。完整十格式模板 590 B，实际配置按选中子集生成，`bcdDevice=0x0200`。
- PROBE、各采样率 floor/ceil 一毫秒帧数的动态音频包、来源绑定、keepalive 和旧 generation 失效；事件与统计
  订阅、有限 IDENTIFY、应答后 REBOOT、双核看门狗。

网络配置通过 TCP 修改前需先停止 USB。UDP 广播恢复的 NET_APPLY 会暂停 USB，
以便确认时安全写 Flash；确认后仍保持停止，用户显式开始模拟。

## 明确限制

业务端口当前固定为 49160/49161/49162/49163（发现/控制/播放/麦克风）；UAC2、32-bit PCM、24-bit / 176.4/192 kHz、
改业务端口、恢复出厂与远程固件升级不提供实现，能力
和命令返回须按实际处理。USB 主机侧 UAC1 mute/volume 由 USB owner 处理，GET_INFO/GET_STATS 回报真实主音量、静音与 revision。同机 Windows 通过匹配 USB serial 的 Host endpoint 写音量；外部 NS/手机可设置板端 Feature Unit，Host 后续写入可覆盖，远端系统 UI 不会被强制同步。

网络使用同步短 SPI burst；没有宣称 DMA、最坏调度期限或全网络压力通过。
WIZnet 底层命令寄存器仍有短轮询，芯片不响应时由双核看门狗恢复。
`audio_packets_sent` 是提交数，`audio_send_completions` 才是硬件 SENDOK；
两者都不代表 PC 播放确认。`RESET_STATS` 只清网络运行计数，USB 计数按
本次 boot 累计，并由返回字段明确说明。

编译、模拟回归和真实硬件测试必须分别记录。测试程序及边界说明见
`tests/README.md`；实机证据由仓库 `artifacts/acceptance` 中的报告提供。

启动策略在 v8 改为上电自运行。v7 首次认证后启动的实机记录保留为历史证据；
新策略须单独验证无管理连接、持久化名称和手动停止后的重连行为。

0.3.0-dev 使用真实 Host alternate/rate 驱动 PCM 与网络格式；高规格 1 ms 为96帧/576B PCM、624B UDP。增益与静音用1ms渐变，源代次重新开始淡入；USB短块结束旧代次，不伪造连续时间轴。此修改不能替代真实Host高规格和爆音长测。
