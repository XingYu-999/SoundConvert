# USB 手动 BOOTSEL 固件烧写

本批上位机增加独立 USB 固件烧写窗口，版本为 V0.12.1。使用 RP2350 固有的 ROM BOOTSEL／UF2 能力，不增加固件命令、USB 接口或板卡升级逻辑，原有固件无需先升级。本批只有上位机功能修改；共享工作区 V0.12.0 的 UDP 端口功能属于另一批修改。

## 使用

1. 运行完整发布目录中的 `client-win-x64/SoundConvertClient.exe`，点击左侧“固件烧写”。未连接音频后台或板卡网络也能使用。
2. 按住板卡 **BOOTSEL** 按钮，再用 USB 数据线连接运行软件的电脑。Windows 出现 RP2350 启动盘后松开按钮。USB 原先接在手机或游戏机上时，需要换接电脑；音频会中断。
3. 点击“选择 UF2…”，选择本项目生成的 RP2350 Arm-S `.uf2` 固件。界面显示文件名、大小，并提供地址范围和所选文件的 SHA256。
4. 从下拉框明确选择目标 RP2350 启动盘。窗口每两秒更新，也可点击“刷新”。启动盘只标识 RP2350 芯片，仍应确认实际接入的是 W5500-EVB-Pico2。
5. 点击“开始烧写”，等待传输结束。传输期间不拔线，窗口关闭、软件退出、文件切换和重复烧写均受保护。
6. 文件传输完成后板卡通常自动重启；重新连接设备，检查实际固件版本、设置保留和 USB 音频运行状态。

## 实现边界

- 通过 Windows 可移动卷的 `INFO_UF2.TXT` 识别 RP2350 BOOTSEL；普通磁盘和 RP2040 启动盘不会作为候选。多块板卡时不自动选定目标。
- 选择设备断开后取消选择，不把新的设备自动作为原目标。写入前重新核对启动盘，并通过已捕获的卷 GUID 打开目标文件，避免盘符变化导致写错卷。
- 镜像按 512 字节 UF2 块解析，检查魔数、RP2350 Arm-S family、块总数、重复编号／地址、256 字节载荷和地址对齐。固件须从 Flash 起始地址连续排列，块编号与目标页一致。仅允许实际固件落在 `0x10000000` 至 `0x101FDFFF`，保护本板 2 MiB Flash 末尾 8 KiB 配置区，拒绝整片擦除镜像、SRAM 镜像和其他芯片镜像。
- 正确支持当前 Pico 工具生成的 `0x10FFFF00` ignored compatibility block。该块有独立块计数，且带 `UF2_EXTENSION_RP2_IGNORE_BLOCK`；仅接受已知完整模式，不将任意 absolute family 镜像放行。
- 校验后在内存中冻结完整镜像；文件之后变化不会更换已经验证的内容。通过标准 Windows 大容量存储写入 `FIRMWARE.UF2`，不依赖 Npcap、串口、DAPLink、OpenOCD 或 picotool。
- 进度 100% 表示全部数据、刷新和文件关闭均已完成。任何打开、写入、刷新或关闭异常都保留失败／无法确认结果，并要求重新选择设备；不会在异常后推断烧写成功。
- SHA256 是所选文件的摘要，不是签名认证。UF2 结构与地址检查不能证明镜像属于某一具体电路板，也不能保证配置记录跨版本兼容。只复制 UF2 无法执行 Flash 读回校验，启动和音频结果仍需实板确认。

## 验证记录

证据目录：`artifacts/acceptance/v0.12.1`。最终结果见 `software-delivery.json`；界面截图、初次检查失败和修正后的结果均保留。

| 检查 | 结果与范围 |
| --- | --- |
| Release 构建／自包含发布 | 前台与 Engine V0.12.1 成功；最终生产构建、发布无警告或错误 |
| 协议／音频／UF2 离线回归 | 最终 1307 项通过，包含新增 56 项 UF2 与内存传输故障检查；部分已有检查数量受采样窗口影响，保留各次原始日志 |
| 现有原生界面回归 | 353 项通过；TCP 板卡替身及注入输出，不连接实板 |
| 独立烧写窗口 | 27 项通过；分别使用 V0.11.11 真实项目 UF2 和当前构建 UF2，传输目标均为内存盘替身 |
| 发布二进制 | EXE 和 Engine 文件版本为 0.12.1.0；实际加载发布 App DLL 运行上述窗口检查，SHA256 与交付 DLL 相等 |
| 配套固件 | 共享工作区的 V0.12.1 构建已核对；本批没有编辑固件源码或烧写板卡 |
| 实板烧写／启动／配置／USB | 未执行，保留待验收 |

初次受限沙箱回环套接字返回 10013，正常 Windows 上下文回归通过。初次按钮检查读取局部 `IsEnabled` 而非命令生效后的 `IsEffectivelyEnabled`，修正检查工具后通过。发布曾遇到 NuGet 审计服务不可达和 Engine RID 资产选择问题；使用现有锁文件及已缓存依赖完成最终发布，未改变依赖。界面检查工具有两条既有 `Bitmap.Save` 过时警告，生产程序没有新增编译警告。

初始板卡源码哈希快照的比较发现共享工作区 `uab_network.c` 被并行 UDP 端口任务更新，见 `firmware-source-comparison.json`。保留该改动，不把整个共享工作区表述为“板卡源码完全未变化”；USB 烧写批次自身没有编辑任何板卡源码。

交付目录为 `artifacts/releases/V0.12.1-usb-flash`，其中 `client-win-x64/SoundConvertClient.exe` 为入口，需保留完整 Engine 子目录。配套固件是共享工作区快照，使用此烧写窗口并不要求先更新原有板卡固件。

检查包括：真实项目 UF2 及兼容块；错误芯片、损坏／缺失／重复块、地址越界及配置区覆盖拒绝；原数组变化后仍传输已验证内容；打开／写入／刷新／关闭失败不能返回完成；目标移除不重定向；有效／无效文件替换；传输互斥；原生窗口和主程序退出保护；最小窗口的按钮可见性。

本批未按键进入真实 BOOTSEL、未向任何实板写入固件、未访问 COM8、未做 Flash 读回或真实 USB 音频验收。内存盘替身与界面检查不能替代这些结果。后续实板验收应记录镜像 SHA256、目标身份、传输日志、启动后版本以及配置／USB 恢复结果。

参考：Raspberry Pi [BOOTSEL／UF2 使用说明](https://www.raspberrypi.com/documentation/microcontrollers/c_sdk.html)、[SDK UF2 头定义](https://github.com/raspberrypi/pico-sdk/blob/2.3.1/src/common/boot_uf2_headers/include/boot/uf2.h)，Microsoft [卷 GUID 路径接口](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw)。
