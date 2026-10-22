# 平台、USB 选型与移植

本文件承接 [总览](README.md)。按最新要求，先完成 Windows 上的 UAC1 单向声音接收，再逐个验证 NS1、NS2、Android、iPhone。UAC2 与麦克风回传只作后续可选扩展，不要求首版实现模式切换。

2026-10-18 源码修订：已按本地 `D:\GithubClone\tinyusb` 的提交 `2f675abdd182163d03d4b25b47ad03497da23b1a` 核对。独立的实施步骤、CMake 增量、宏配置、描述符、请求处理、反馈与恢复任务见 [USB 声卡开发指南](08-USB声卡开发指南.md)。本文件保留平台选型和主机兼容范围。

## 开发板能力与接线

| 项目 | 已核实信息 | 对项目的影响 |
| --- | --- | --- |
| MCU | RP2350，双 Cortex-M33，最高标称 150 MHz，520 KB SRAM | 首版选 Arm 平台，避免同时引入 RISC-V 工具链差异 |
| 板载 Flash | 板页标称 16 Mbit，即 2 MiB | 不能直接套用某些 Pico 2 模板的 4 MiB 假设；以实物 Flash、BOM 与链接配置复核 |
| USB | 原生 USB Full-Speed 12 Mbit/s，Device/Host 控制器 | 本项目固定 Device；没有 480 Mbit/s USB HS |
| Ethernet | W5500，10/100 Mbit/s MAC/PHY，SPI 接口 | 音频带宽充足与否要按 SPI、CPU 和包处理实测，不能只看 RJ45 速率 |
| W5500 存储 | 合计 32 KiB TX/RX；8 个硬件 Socket | 不等于 RP2350 SRAM；MACRAW 方案不按 8 个业务端口分硬件 Socket |
| 调试 | SWD、板载 LED | USB 给音源占用时仍可通过独立探针调试 |

硬件信息来源：[WIZnet 板卡页面](https://docs.wiznet.io/Product/Chip/Ethernet/W5500/w5500-evb-pico2)、[RP2350 官方规格](https://www.raspberrypi.com/products/rp2350/)、[W5500 官方规格](https://docs.wiznet.io/Product/Chip/Ethernet/W5500)。板卡页面部分电源说明混用了旧板名称，电源改造应以所购 PCB 版本的原理图为准，不能把 RP2350 GPIO 当成 5 V 通用输入。

W5500 专用管脚如下，不能再分配给其他外设：

| RP2350 GPIO | 板内连接 |
| --- | --- |
| GPIO16 | SPI MISO |
| GPIO17 | W5500 CSn |
| GPIO18 | SPI SCLK |
| GPIO19 | SPI MOSI |
| GPIO20 | W5500 RSTn |
| GPIO21 | W5500 INTn |
| GPIO24 | 官网称 VBUS 检测；V1.0 图中分压接 D2 后/VSYS，不作为独立供电下物理拔线的可靠判据 |
| GPIO25 | 用户 LED |

USB 数据口接音源主机，RJ45 接交换机或电脑网口。手机应通过真正支持 USB Host 的数据线/OTG 适配器连接。Lightning iPhone 使用支持 USB 音频设备的相机转接器；供电不足时使用带供电的转接器或合规有源 Hub。USB-C iPhone 支持音频附件，但这不构成本板所有档位都兼容的证明。[Apple USB 相机适配器](https://support.apple.com/en-us/111811)、[iPhone USB-C 连接](https://support.apple.com/en-nz/105099)

同时使用外部供电、PoE 附件和 USB 供电前检查电源路径，避免回灌；没有 PoE 模块不能因为 RJ45 存在就认为能直接 PoE 供电。首版无需 DAC、ADC、I²S Codec。

## 推荐开发环境

### Windows 上的工具与插件

| 工具或插件 | 标识/来源 | 用途与必要性 |
| --- | --- | --- |
| VS Code | Microsoft 官方发行版 | 推荐编辑器 |
| Raspberry Pi Pico | `raspberry-pi.raspberry-pi-pico` | 必装；管理 SDK、Arm 工具链、CMake/Ninja、项目和调试配置 |
| C/C++ | `ms-vscode.cpptools` | C/C++ 补全和调试基础；先看 Pico 扩展是否已作为依赖安装 |
| Cortex-Debug | `marus25.cortex-debug` | SWD 断点、寄存器、调用栈；检查扩展依赖后补装 |
| CMake Tools | `ms-vscode.cmake-tools` | 可选；启用 Pico 扩展的对应集成后再用，避免两个配置器争用构建目录 |
| C# | `ms-dotnettools.csharp` | 后续 .NET 上位机编辑与语言服务 |
| C# Dev Kit | `ms-dotnettools.csdevkit` | 可选；使用前核对适用授权 |
| Avalonia 扩展 | 在 Marketplace 按 Avalonia 官方发布者选用 | 可选 XAML 工具；预览/设计功能及授权依具体发行版核实 |
| Git for Windows、Python 3 | 各项目官方来源 | 获取依赖和运行上游辅助脚本 |
| Wireshark、USBView、调试探针 | 官方发布渠道 | 网络分析、USB 描述符检查、SWD 调试 |

Pico 扩展确实支持项目生成、Ninja 和工具路径配置，建议建立独立 VS Code Profile，减少其他嵌入式扩展的环境冲突。[官方 Marketplace 页面](https://marketplace.visualstudio.com/items?itemName=raspberry-pi.raspberry-pi-pico)

CLion + Pico SDK 可以作为付费替代。PlatformIO/Arduino 更适合已有经验者做短期原型，但应先核对当前 RP2350 平台包、USB 栈版本和自定义描述符能力。MicroPython 不作为本项目的 USB ISO 与低延迟数据主路径；可以用于外围实验，不能用解释执行方案推定实时性。

### 创建一个能调试的基础工程

当前已存在 `SoundConvert/SoundConvert` 基础工程：CMake 选用 SDK `2.3.1`、工具链配置 `15_2_Rel1`、`PICO_BOARD=pico2`，启用 UART、禁用 USB stdio；源码仍为 SPI/内存 DMA 与定时打印示例，尚未接入 TinyUSB。继续使用这个工程，USB 接入步骤见文档 08。以下创建步骤仅供重新搭建环境时参考，本次未修改工程源码：

1. 安装 Pico 扩展，打开左侧 Raspberry Pi Pico 面板，选择 **New C/C++ Project**。
2. 名称可设为 `UsbAudioBridge`；选择 **Pico 2 / RP2350 Arm**，不要选 Pico/RP2040、Pico 2 W 或 RISC-V。
3. 选择一个正式 Pico SDK 2.x 版本，记录版本号。网络示例若依赖其他 SDK，先保留其原配版本做独立验证。
4. 调试器选择所持有的 CMSIS-DAP/Debug Probe。日志首选 UART 或 SWD RTT；正式声卡不要默认混入 USB CDC。
5. 编译最小 LED 工程。按住 BOOTSEL 后插入电脑，将 UF2 写入 ROM 启动盘；再使用 SWD 验证下载、复位和断点。
6. Pico 2 模板只是起点：核对 WIZnet 板的 Flash 容量、GPIO25 LED、W5500 GPIO16–21、晶振与启动配置，后续建立本板独立 board 配置。
7. 固定工作目录、SDK 路径和工具链。不要在一个 CMake build 目录内反复切换 RP2040/RP2350 或不同 SDK。

官方步骤参考：[Pico VS Code 扩展介绍](https://www.raspberrypi.com/news/pico-vscode-extension/)。同一个项目只能有一处启动初始化、一份 USB 中断处理和一套选定的 TinyUSB 来源。

### WIZnet 网络示例创建方式

先单独验证网络，与 USB 音频示例分开保存。使用 WIZnet 硬件 Socket 示例仓库：

```powershell
git clone --recurse-submodules https://github.com/WIZnet-ioNIC/WIZnet-PICO-C.git
```

在 VS Code 中导入该目录，按所选 SDK 配置工具链。**本次核实的仓库在顶层 CMake 中直接设置 `BOARD_NAME`**，当前默认不是 W5500-EVB-Pico2。应在将来创建的工作副本中将唯一有效板型设为 `W5500_EVB_PICO2`，由该分支选择 RP2350 和 W5500。

不能只在命令行传 `-DBOARD_NAME=W5500_EVB_PICO2` 就假定覆盖了普通 `set(BOARD_NAME ...)`。这与 Pico SDK 的 `PICO_BOARD`、TinyUSB 的 `BOARD` 是三套不同的选择入口。[硬件 Socket 示例 CMake](https://raw.githubusercontent.com/WIZnet-ioNIC/WIZnet-PICO-C/main/CMakeLists.txt)

从 Pico 扩展提供的工具环境执行：

```powershell
cmake -S . -B build -G Ninja
cmake --build build --target help
cmake --build build
```

选择仓库实际存在的 DHCP/UDP loopback 示例，确认获取地址、收发 UDP、拔插网线恢复。编译目标名称以该次锁定版本的 `--target help` 为准，不把某个历史示例目录名硬写成通用目标。正式项目采用同一硬件 Socket 路线，移植步骤和收发等待改造见 [独立以太网文档](06-单片机以太网开发指南.md)。

### TinyUSB 音频示例创建方式

现有 clone 位于 `D:\GithubClone\tinyusb`，分支 `master`，HEAD 为 `2f675abdd182163d03d4b25b47ad03497da23b1a`，`git describe` 为 `0.21.0-605-g2f675abdd`。**以这份实际提交为开发基线，取代旧文“先 clone 0.21.0”的步骤**；源码版本宏仍显示 0.21.0，不能因此把当前代码当成正式标签。升级前保留完整 SHA 和回归记录。

```powershell
$tinyUsbRoot = 'D:\GithubClone\tinyusb'
git -C $tinyUsbRoot rev-parse HEAD
git -C $tinyUsbRoot describe --tags --always --dirty
git -C $tinyUsbRoot status --short
```

在 Pico 工具环境中分别做上游对照构建和应用集成构建，命令见 [文档 08 第 3 节](08-USB声卡开发指南.md#3-工程接入步骤)。上游 Pico 2 board 选择 `rp2350-arm-s/pico2`；family 的 `CFG_TUSB_MCU` 仍是 `OPT_MCU_RP2040`，这两个系列共用该驱动入口，不要自行改成不存在的 RP2350 宏。[固定提交 board](https://github.com/hathach/tinyusb/blob/2f675abdd182163d03d4b25b47ad03497da23b1a/hw/bsp/rp2040/boards/raspberry_pi_pico2/board.cmake)、[固定提交 family](https://github.com/hathach/tinyusb/blob/2f675abdd182163d03d4b25b47ad03497da23b1a/hw/bsp/rp2040/family.cmake)

已读取本机 SDK `2.3.1` 的 `src/rp2_common/tinyusb/CMakeLists.txt`：它支持 `PICO_TINYUSB_PATH`，通过 `tinyusb_device`/`tinyusb_board` 接入外部栈。配置阶段明确指定 clone，核对输出与 `compile_commands.json`；仅复制仓库到硬盘不会改变已有工程实际链接的 TinyUSB。纯 USB 对照也要核对本板 2 MiB Flash，不能沿用 Pico 2 默认 4 MiB。

教程以 clone 内 `docs/integration.rst` 和 `docs/porting.rst` 为准。前者适合应用集成，后者讲为新 MCU 写 DCD；本项目复用既有 RP2350 支持。当前 DCD 自动安装 USB IRQ，不能照搬通用教程再注册一次中断，也不递归加入所有厂商驱动。

当前 `uac2_speaker_fb` 的 FS 分支确实是 UAC1：`CFG_AUDIO_DEBUG` 默认开启并带 HID，描述符宣告 44.1k/48k，初值 44100；`audio_task()` 按毫秒读取 dummy buffer 且忽略实际返回长度。首版须关闭 HID，把描述符频率数量/总长度、初值、请求白名单与反馈统一到 48k，再以实际读到的 PCM 接应用队列。[当前示例源码](https://github.com/hathach/tinyusb/tree/2f675abdd182163d03d4b25b47ad03497da23b1a/examples/device/uac2_speaker_fb/src)

FIFO feedback 需要稳定本地消费时基；网络阻塞不能阻止消费，网络可发也不能触发清空 FIFO。首版名义 192 B/ms、最大 USB 包 196 B；软件 FIFO 可从 784 B、目标水位 384 B 起验证。当前示例为兼顾 HS 实际分配 1664 B，不能假定它就是最低延迟配置。当前类 FIFO 允许覆盖旧数据，必须统计/检测失连续；当前收包回调为 `tud_audio_rx_done_isr()`，不沿用旧版 pre/post-read 签名。

## USB 开源协议栈比较

这里讨论的是**板端 USB Device 栈**；PC/手机侧通常使用系统 USB Audio 类驱动。libusb、WinUSB 不会自动让 NS 或手机把板子识别为声卡。

| 方案 | UAC 与 RP2350 可行性 | 实时性特点 | 难度 | 资源与维护 | 结论 |
| --- | --- | --- | --- | --- | --- |
| Pico SDK + TinyUSB | 上游列出 RP2350 与 UAC1/UAC2；需实现本板档位 | 静态缓冲、任务处理 USB 事件；及时运行 USB task 是关键 | 中 | 可裁剪；RAM 主要受端点/FIFO/应用队列影响 | 主方案 |
| Pico SDK + CherryUSB | Device 声明 UAC1/UAC2；有 RP2040 端口，RP2350 精确版本仍需核对 | 端点回调路径较直接，可做短 ISR；长回调同样有风险 | 中高 | 栈可裁剪；需承担 RP2350 端口及恢复测试维护 | 备选验证分支 |
| Arduino-Pico + 其 USB 集成 | 取决于锁定 core 和底层 TinyUSB 版本 | 可以运行原生 C/C++，但封装与后台任务增加可见性成本 | 原型低、深度定制中高 | 自定义双 UAC 描述符、生命周期可能需深入 core | 不作为完整开放配置的首选 |
| RTOS + TinyUSB/CherryUSB | RTOS 是调度层，不是另一个 UAC 驱动 | 优先级可控，但 tick、锁、跨核机制可能引入抖动 | 中高 | 增加任务栈及同步成本 | 复杂度确实增长后再引入 |
| 自写 RP2350 USB/UAC 栈 | 理论可行 | 可精确控制，但枚举、ISO、反馈、异常恢复都需自证 | 很高 | 代码少不代表验证成本低 | 不建议 |

来源：[TinyUSB 项目](https://github.com/hathach/tinyusb)、[CherryUSB 项目](https://github.com/cherry-embedded/CherryUSB)、[CherryUSB RP2040 Device 端口](https://raw.githubusercontent.com/cherry-embedded/CherryUSB/master/port/rp2040/usb_dc_rp2040.c)。不把 CherryUSB 的 RP2040 驱动文件存在，直接当成 W5500-EVB-Pico2 已通过音频测试。

两栈没有本板相同固件、相同缓冲下的测量结果，不能给出“某栈快几毫秒”或精确占用排名。评估时应统一 48 kHz/16-bit/2ch、编译优化、日志、DMA 和网络负载，比较 ELF/map、最坏服务间隔、USB 丢帧和 CPU 使用率。

### TinyUSB 移植工作清单

1. 建立 `board` 适配职责：时钟、VBUS、LED、UART、唯一标识、Flash 容量；复用 DCD 自动安装的 USB IRQ 与芯片适配，不再创建第二个 handler。
2. 只启用 Audio Device 必需类，使用明确的端点与 FIFO 大小；关闭示例自带 HID debug、MSC、CDC 等非必要接口。
3. 首版只维护 UAC1 描述符，限制到已验证的 48k/16/2ch 播放。USB reset 后按已提交配置固定一次，不在控制请求中随意更换描述符内存；UAC2 描述符集以后有需求再做。
4. 在 Core 0 以稳定消费时基读取 OUT FIFO，按实际返回长度向 SPSC 队列交接完整 PCM 帧；RX ISR 只计数/标记。不发网络、不写 Flash、不阻塞日志、不等待 PC。
5. 处理配置、`SET_INTERFACE`、采样率请求、静音/音量、挂起、恢复、拔插和 bus reset。审查示例控制请求的 `wLength`、channel/entity/interface 索引和范围检查，不将演示代码直接当成经过畸形请求验证的产品实现。
6. 让网络核心消费队列。网络阻塞时按策略丢弃过期数据，继续服务 USB。
7. 使用示例做控制请求对照，再删去其测试音发生器、回环与调试功能；示例不是完整网络声卡产品。
8. 固化 USB 描述符转储、主机系统版本、板端统计，作为每次升级 TinyUSB 的回归基线。

生命周期需额外覆盖：当前 DCD 固定 `FORCE_VBUS_DETECT=1`，拔线可能表现为 suspend；USB reset 也不保证调用 `tud_umount_cb()`。按文档 08 使用事件 hook、经验证的连接观测和状态收敛使旧 PCM 代次失效，不把 umount 或 GPIO24 单独当成可靠拔线证明。参数写入以应用完成“停止模拟”为条件。控制请求中已有部分 channel/长度检查，但固定采样率白名单、Q8.8 音量一致性和实际增益仍需产品实现。

### 可选扩展：UAC2 Full-Speed

本次直接核对的 `uac2_speaker_fb` 采用 **FS 返回 UAC1、HS 返回 UAC2** 的策略。RP2350 没有 HS，因此直接烧录不能证明“UAC2 模式可选”。[固定提交 speaker 描述符](https://github.com/hathach/tinyusb/blob/2f675abdd182163d03d4b25b47ad03497da23b1a/examples/device/uac2_speaker_fb/src/usb_descriptors.c)

只有将来决定增加 UAC2 时才需要：按固件档位而非总线速度选描述符；保留 FS 的包长和周期；实现 UAC2 Clock Source 与请求；核对反馈格式及 Windows 类驱动要求；FS-only 不伪造 HS Device Qualifier/Other-Speed 能力；逐个验证 UAC2-FS 主机兼容性。不能只把 `bcdADC` 从 1.0 改为 2.0。即使扩展，也可以先用单独 UAC2 固件验证，不必立即增加运行时切换。

### CherryUSB 备选移植步骤

锁定版本 → 检查 RP2350 时钟/USB 寄存器及 PHY 初始化差异 → 接入 Pico SDK 启动和 USB IRQ → 先跑 CDC 枚举 → 替换成 UAC1 播放 → 验证 ISO 连续收包与断连 → 接同一 PCM 队列 → 跑与 TinyUSB 完全相同的测试矩阵。以后如需要再加 UAC2-FS。不得让两套栈同时注册同一控制器中断。

## USB 档位与主机兼容策略

| 档位 | 描述 | 主要目标 | 实施顺序 |
| --- | --- | --- | --- |
| `compat-uac1` | UAC1、48k、S16LE、2ch OUT、无麦克风、最少接口 | NS、Android、iPhone、PC 的基础枚举 | 默认出厂档 |
| `desktop-uac1` | UAC1，增加 44.1k；可选 1ch IN | PC 与支持录音的手机应用 | 后续可选 |
| `extended-uac2` | UAC2-FS、Clock Source、48k 为基础；24-bit/96k 按预算开放 | 已验证 PC/移动主机 | 后续可选，不要求可切换 |
| `custom` | 能力允许范围内组合 | 高级用户与兼容性实验 | 已实现范围内开放，全部校验 |

首轮优先沿用 TinyUSB 示例已有的 UAC1 OUT 与反馈路径，减少同时更改栈、描述符和同步机制的变量。纯数字桥没有本地 DAC，但保留异步 OUT + 显式反馈时仍必须定义本地消费速率，并验证反馈端点；不得把 UDP 拥塞或电脑瞬时缓冲变化直接反馈成 USB 采样率。

若某主机不能兼容该反馈档，再单独验证基于 USB SOF 的最小同步 OUT 档；不要求首版同时维护多个同步档位。选择同步 OUT 后相应移除反馈语义，并由 PC ASRC 补偿源/输出时钟差异，不能只删除反馈端点却保留异步描述。默认推荐不等于实测通过。

| 主机 | 连接与验收重点 | 证据边界 |
| --- | --- | --- |
| NS2 | 官方底座 TV 模式，直连 USB，48 kHz；记录主机与底座固件 | 官方不保证所有 USB 音箱；手持/桌面模式不列入承诺范围 |
| NS1 | 底座及拟支持的其他模式分别实测；先最小 UAC1 播放 | 不能拿 NS2 或第三方旧实验代替 NS1 验证 |
| Windows PC | 首版 UAC1；休眠、默认设备、名称缓存；以后 UAC2 单独验证 | Windows 10 1703 起有系统 UAC2 驱动；更早系统单独评估 |
| Linux/macOS PC | 系统 Audio class、格式切换与挂起恢复 | 没有实际测试记录前仅列为待验证 |
| Android | USB Host/OTG、供电、系统路由、应用是否独占 USB | AOSP 公开基础保证是 UAC1 子集，厂商 UAC2 扩展需实测 |
| USB-C iPhone | 数据线、供电、锁屏、来电/路由切换、应用播放与录音 | 接口支持音频不代表任意描述符都通过 |
| Lightning iPhone | 相机转接器、外部供电、系统版本 | 普通 Lightning 充电线不是 USB Host 音频转接器 |

依据：[Windows UAC2](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/usb-2-0-audio-drivers)、[Android USB audio](https://source.android.com/docs/core/audio/usb)、[NS2 官方限制](https://en-americas-support.nintendo.com/app/answers/detail/a_id/68276/p/1095/c/990)。手机通话、受保护内容、游戏聊天麦克风等由系统和应用另行决定，不能从“系统能播音乐”推定全部场景可用。

## 带宽与描述符校验

PCM 每秒字节数 = 采样率 × 声道数 × 容器字节数。FS ISO 单端点每帧最大载荷 1023 字节，此外还受全总线周期预算约束。[Microsoft ISO 传输说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/transfer-data-to-isochronous-endpoints)

| 格式 | PCM B/s | 每 1 ms 名义载荷 | 结论 |
| --- | ---: | ---: | --- |
| 48k/16-bit/2ch | 192,000 | 192 B | 默认 |
| 48k/24-bit packed/2ch | 288,000 | 288 B | 可评估 |
| 48k/24-in-32/2ch | 384,000 | 384 B | 容器占 4 字节，不能按 24-bit 计算带宽 |
| 96k/24-bit packed/2ch | 576,000 | 576 B | 单方向端点容量允许，仍需周期和 CPU 实测 |
| 192k/24-bit packed/2ch | 1,152,000 | 1,152 B | 超过单 FS ISO 端点上限，拒绝此普通单端点组合 |

44.1k 需要 44/45 个采样帧交替，不能固定每毫秒截成 44。异步反馈档还可能出现额外一帧，`wMaxPacketSize`、FIFO、接收长度都必须按最大包计算。USB 包与 UDP 包边界不要求相同，网络以连续 sample index 表示时间。

正式发布前按 USB-IF 对应 Audio/USB 规范复核描述符与周期预算，不能仅凭以上载荷算式推定合规。多方向、多声道需合计 ISO 与其他端点开销；UAC2 不扩大 FS 总线容量。

## 名称、身份与开放配置

USB 展示名用 `iProduct`；厂商名用 `iManufacturer`；接口/终端名分别提供。网络设备别名、USB 名称、PC 自定义别名是三个独立字段。

USB 声卡出厂默认名称固定为开发板名称 `W5500-EVB-Pico2`，对应 `usb.product_name` 与 `iProduct` 指向的字符串；`usb.playback_name` 默认同名，播放接口/终端中如提供展示字符串也使用该值。厂商名仍独立配置。首次初始化或明确恢复出厂时采用这些默认值，读取配置、升级或省略字段的更新保留已保存的自定义名称。

项目建议名称上限 63 个 UTF-16 code units，内部用 UTF-8 配置，编码时正确处理代理项，不将 UTF-8 字节逐个扩成 UTF-16。兼容档先用 ASCII，中文等字符由主机矩阵验证。上位机必须先停止模拟并收到 STOPPED，再修改/保存；保存后保持停止，下次用户开始模拟时 Host 重新枚举新名称。Windows 可能缓存名称，验收同时查看实际描述符。

同一限制覆盖全部上位机 USB 声卡参数写入、导入及恢复出厂。运行中不能只靠停播或软件静音解锁；USB Host 的正常 UAC 控制与电脑本地监听音量仍独立运行。开发板上电加载已保存配置后自动枚举声卡，不等待网络或上位机认证；手动停止后需显式开始，整机再次上电则重新自动开始。完整状态、命令和错误码见文档 03，固件动作见文档 08。

保持稳定且唯一的 `device_id`、MAC 和序列号。不要为每次改名生成新序列号；为 UAC1/UAC2 或拓扑差异规划稳定的 PID 映射可减轻驱动缓存冲突。VID/PID 的分配权、第三方标识使用范围应在产品化时核实，不能直接把示例 VID/PID 当作自有量产身份。

“完全开放”是所有已实现能力可查询、可配置、可导出，并明确限制与生效时机；不接受超出硬件能力的任意描述符字节。完整字段、范围、事务和错误返回见 [协议配置契约](03-通信协议规范.md)。
