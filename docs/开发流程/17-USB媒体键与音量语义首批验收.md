# V0.5.0 USB 媒体键与音量语义（第一批）

日期：2026-10-20。按开发计划首批实现阶段 B 与 D.1；根目录 VERSION 从 0.4.0 递增一次至 0.5.0。阶段 C、F、D.2 和 E 仍为后续批次，不把第一批记录解释为整个计划已完成。

## 已实现

- 固件接受 `play_pause`、`next_track`、`previous_track`、`volume_up`、`volume_down`、`mute`，Consumer Usage 分别为 0xCD、0xB5、0xB6、0xE9、0xEA、0xE2。协议白名单与能力列表来自同一张表；保留 8 项有界 FIFO、10 ms 按下后释放、重连先发中立报告。
- USB 断开、总线重置、挂起时清空按键；队列项携带代次，防止跨核入队恰好遇到断开时把旧按键带到下一次连接。USB 回调只做有界状态处理，网络仍在 Core 1，HID 发送仍在 Core 0。
- 独立后台经现有前后台协议处理六种命令。每个按键等待各自的网络结果，不进入滑块的最新值合并器。连接、管理权限、USB RUNNING、mounted、suspended、HID ready 和能力白名单共同决定可用性，不依赖 streaming 或 PCM。
- 命令确认只表示开发板接受入队；HID 完成计数表示按下／释放传输完成。均不代表播放器状态；界面不伪造确定的播放／暂停状态，也不自动重放结果未知的请求。
- 主页、播放页及虚拟声卡页固定分开显示 USB Host 控制和板端增益。本机匹配到序列号端点时，Host 滑块为真实 Windows 端点百分比；远端显示系统音量未知，使用 Host 音量键。板端增益固定为 −60～0 dB、独立静音，`USB_SET_VOLUME` 只写 Feature Unit。电脑输出音量继续控制所选 Windows 输出端点。
- Host 后续改写 Feature Unit 时，界面按实际读回收敛；读回不产生反向写入。连续增益仍保留最新值合并，已发请求收完响应。

## 协议和使用

见 [通信协议](03-通信协议规范.md)。完整解压 `artifacts/releases/V0.5.0/client-win-x64`，运行 `SoundConvertClient.exe`，保留 Engine 子目录。连接配套固件后在主页或播放页操作；暂停后只要 HID 保持就绪，播放／暂停仍可用。

旧固件仅声明 `host_volume_keys_supported` 时只开放旧音量键；没有 HID 就绪状态时全部按键禁用。前后台版本不一致沿用现有拒绝连接策略，不默默使用旧后台执行新命令。非法值、设备队列满、状态失效和结果未知均有明确反馈。

按键值依据 [USB-IF HID Usage Tables 1.5](https://www.usb.org/sites/default/files/hut1_5.pdf)。已复核 [Android USB 耳机规范](https://source.android.com/docs/core/interaction/accessories/headset/usb-headset-spec)：Headphone／Headset 顶层集合及终端类型要求需与阶段 C 协同。第一批保留现有桌面音箱终端、Consumer Control 集合和 USB 标识，不声称 Android 耳机规范兼容或所有 Host 响应。未开放独立播放、独立暂停和停止。

## 实施前基线

- 根目录源码版本 V0.4.0；当前目录没有可用 Git 元数据，保存源码 SHA256 清单、原 V0.4.0 发布产物哈希。原始证据：`artifacts/acceptance/v0.5.0/baseline`。
- 当前 PC 有线 IPv4 为 192.168.0.23；与文档16的历史验收网段不同，没有沿用历史设备 IP 或耳机端点。
- COM8 / 115200 / 8N1 可读，当前串口实测 `usb=RUNNING mounted=0 streaming=0`。Windows PnP 快照未发现开发板 USB 音频/HID 接口，最初没有可用耳机端点。用户随后明确要求直接用扬声器；本批实际输出为 Realtek(R) Audio，端点尾标识4de9d92f-95af-4e99-9d5f-775750773f1e，原音量15.000000596%、未静音。DAP 调试器存在不等于声卡 USB 已接到 Host。
- 随后用户接入手机：小米14 / Android 16 / OS 3.0.307.0.WNCCNXM.C11，QQ音乐（应用版本未提供），截图已留存。所选网卡只读发现到 aa36483cef4256f169f4f4e5322c6df8 / 192.168.0.22，实读 V0.4.0，USB serial=E53B126BB2E31A4A，24-bit/48 kHz，配置 revision=53。完整2 MiB Flash和末尾8 KiB配置区已备份；源码/产物、实际板上版本分开留证。

## 验证结果

实际构建、测试结果与文件哈希记录在发布目录及 `artifacts/acceptance/v0.5.0`。固件编译、生产 ELF 描述符检查、宿主状态机和设备替身均属于离线证据。

| 验证 | 结果 |
| --- | --- |
| RP2350 固件与 Release 客户端构建 | 通过；Windows x64自包含前台、后台与Runtime文件版本0.5.0.0；pico2 / rp2350-arm-s / 2 MiB Flash |
| Core 协议／音频回归 | 最终Build-All日志838项通过；检查数含时序相关循环，初次运行849项，均为离线通过 |
| HID 生产状态机 | 109 项通过：六种 Usage、非法值、队列满、按下／释放、传输失败、断开、挂起、计时回绕 |
| 其他固件宿主回归 | USB owner 24251、麦克风105、描述符过滤1023、生产ELF过滤1023、配置29、JSON精确数值30750；boot、DHCP、TX、纠错、ADPCM、watchdog等全部通过 |
| 生产 ELF 描述符 | 1033 字节 UAC1 + HID 模板、四接口、0x01/0x81/0x82/0x83 端点和 16-bit Consumer 报告通过 |
| 原生界面与协议替身 | 184 项通过，含无 PCM 媒体键、六键顺序、旧固件、非法键、队列满、挂起、未枚举、HID 未就绪、只读权限、Host 改写增益、丢失响应不重放 |
| 独立后台／原生 UI 集成 | 最终422项通过；六键经原生前台与独立后台IPC逐项到达，发送期间即时禁用重复点击，双滑块、慢应答与前台重接通过 |
| 烧录／USB 枚举／播放器实际响应 | DAP Verified OK；COM8回报V0.5.0；Android mounted、HID ready及六键能力实读；用户确认QQ音乐播放／暂停和下一曲生效，其他结果继续单列 |
| 播放及双向音频连续性 | android-speakers-window-2通过11项实板断言；24-bit/48 kHz扬声器播放，板端及PC所列连续性计数增量全0；双向音频尚未验收 |

## 实板进度与剩余范围

本批已完成上述实际目标和配置冻结、完整Flash备份与部署。配置revision保持53，烧录前后GET_CONFIG完全相同，USB序列号不变。用户明确改用扬声器，因此后续扬声器结果不记作耳机验收。初始没有完整PC音频基线，不能把本批结果写为完整前后性能对比。

媒体键网络证据位于android-*.json，用户观察单独记录在android-host-observations.json；PCM停流与播放器状态分开记录。后续仍需补充上一曲及静音的用户观察、USB拔插／挂起、麦克风并行窗口。Windows、NS2及其他Android播放器均未验证，不把HID完成计数扩展为Host兼容性结论。

独立后台替身的两个早期窗口因测试装置未隔离本地监听、缺少无PCM应答而误触发重连，保留为失败候选；最终engine-ui-final-3的422项通过。没有为通过替身测试修改生产音频恢复逻辑。


## 最终实板窗口与配置保留

正式窗口直接使用 `artifacts/releases/V0.5.0/client-win-x64/Engine/SoundConvertEngine.exe` 与已校验烧录的固件。USB generation前后均为35，未在统计区间切歌或暂停。

| 阶段 | 秒数（含收敛） | UI更新次数 | 板端音量revision增量 | 输出欠载 |
| --- | ---: | ---: | ---: | ---: |
| 仅板端Feature Unit增益 | 15.7297 | 966 | 141 | 0 |
| 仅电脑扬声器音量 | 15.7181 | 966 | 0 | 0 |
| 同时调节两处 | 15.7210 | 968 | 179 | 0 |

原始阶段字段host-only是测试工具沿用的字段名，本批实际绑定并写入的是BoardVolumeDb，不能解释为已测得远端系统百分比。整段板端usb_expired_frames、usb_queue_drops、usb_underruns、usb_resyncs、audio_packets_dropped、audio_pending_expired、audio_send_timeouts增量全0；完成53621个非单位增益块、320次音量revision变化。PC missing_frames、output_underrun_callbacks、resync_count、read_deadline_misses、overflows增量全0，渲染2589120帧。前台断开5秒期间后台持续发包；重接沿用同一后台PID，没有重建控制会话。

`android-speakers-window-1`是重新枚举后等待PCM超时，断言数0，没有音频样本，既不算连续性通过，也不算已经测得的音频丢帧。其后一次新的、明确的恢复播放操作恢复了24-bit/48 kHz PCM；没有重放未知请求。

最终板端增益为0 dB、非静音；最初PC快照为15.000000596%、非静音，正式窗口自己的快照为15.999999642%。窗口清理后另按最初基线恢复15.000000596%，实际读回已核对，见speaker-final-restore.json。系统默认端点和输出格式没有修改。测试数据使用隔离目录，没有覆盖真实用户设置。

通过DAP读回实板RAM中的活动描述符：273字节、四个接口，接口3为HID、interrupt IN 0x83、2字节报告；包含播放OUT 0x01、反馈0x81、麦克风IN 0x82。证据是实板RAM与已校验Flash，不是Android USB总线抓包。最后重启恢复测试前运行格式mask=147；GET_CONFIG仍为revision 53，完整配置内容与基线相同。8 KiB配置区逐字节一致，SHA256前后均为`cdc34c36eabc07ba0fcad7234ebb16b49ce69960130393d617953dcb53dc81ec`。完整Flash备份留在baseline目录，未打包进公开发布文件。

## Android功能证据边界

| 操作 | 固件接受／HID传输 | 实际播放器／系统反应 |
| --- | --- | --- |
| 播放／暂停 | 已完成，含PCM空闲时按键 | 用户确认QQ音乐切换；后续恢复操作实读到PCM恢复 |
| 下一曲 | 已完成 | 用户确认切换到下一首 |
| 上一曲 | 已完成 | 已发出确认问题，尚未收到用户观察结果 |
| 音量减／加 | 两轮传输均完成；第二轮各一次、间隔2秒 | 用户回复“有变化”，确认手机音量实际响应；系统百分比保持未知 |
| 静音 | 离线覆盖 | 本批未做实际Host响应验收 |

以上短时扬声器窗口不扩展为耳机、所有Android、Windows／NS2、所有格式、麦克风并行、USB拔插／挂起恢复或长时间稳定性通过。第一批没有实现设备类型配置、电平扩展、麦克风Feature Unit、UAC状态中断或歌曲元数据。

诊断入口为tools/HostMediaProbe：`--local`必须明确指定所选NIC；省略`--host`只读发现，指定`--host`且不加`--key`只读保存身份／配置／计数；`--key`每次只发送一个明确的键，不自动重试，也不把传输计数写成播放器确认。

2026-10-20 补充验收：用户要求再次测试音量键，21:41:04起依次发送volume_down、volume_up，间隔2秒；HID完成计数0→1→2、待处理0。用户随后回复“有变化”，手机Host音量键实际响应记录为通过；不据此推断系统百分比，也不扩展为上一曲或静音通过。此项仅补充同一V0.5.0批次的验收证据，不修改代码或递增版本。
