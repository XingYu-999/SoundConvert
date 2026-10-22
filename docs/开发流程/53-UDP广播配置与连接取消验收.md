# V0.12.11 UDP 广播配置与连接取消

日期：2026-10-22。范围：客户端和固件源码修改、Windows x64 自包含发布、UF2 构建及软件替身验证。本轮没有烧写实板、改变电脑网卡 IP 或执行实际设备配置写入。

## 修改结果

- 设备页和进度提示提供“取消连接”“取消搜索”。取消命令可在操作忙碌时发送，取消令牌传入实际后台任务和网络等待。
- 连接失败或取消会释放会话、清除当前板卡状态和忙碌提示；握手后的初始化读取也可取消。一次连接设 20 秒总取消期限，TCP/HELLO 另有 5 秒超时。音频设备异步信息读取卡住时也可结束连接等待。同步系统驱动调用的物理故障不由替身测试覆盖。
- 修正 IPC 接受任务与注册完成等待之间的竞态，避免管道中断时遗留永不完成的任务。界面丢失后台时清除过期 IsBusy/IsConnecting；未发送命令不重放。发起端管道关闭会取消其仍未结束的连接和搜索，已完成连接保持现有生命周期。
- 发现和未连接时的 IP 配置统一使用 UABD UDP 广播；仍保留设备 ID、MAC、boot、revision、challenge、事务 ID 校验，以及 prepare/apply/confirm/rollback。结果未知保留事务，重试同一个写请求时保留请求 ID 与字节内容。
- 删除本机抓包驱动加载、捕获和自定义 Ethernet 发现/配置代码，以及依赖抓包驱动的 WireTrace 工具。固件 MACRAW 仅保留 ARP 地址冲突探测。正常 TCP 管理与 UDP 音频沿用现有通路。

## 广播协议和使用边界

电脑在选定 IPv4 上向 `255.255.255.255:49160` 发送请求；非关键 TLV `0x0010` 为 2 字节大端回复端口。接收套接字绑定 ANY，通过 IP_PKTINFO 验证接收网卡；Windows 发送套接字绑定精确源 IPv4，并以 IP_UNICAST_IF 限定接口。

板卡在网线链路就绪后处理发现和维护，即使 DHCP 未分配地址也保持该入口。回复发往广播地址及请求的回复端口，避免旧子网单播回复依赖网关或 ARP。候选网络验证成功后才确认保存；静态确认期限 30 秒、DHCP 120 秒，逾期回滚。应用候选网络会停止 USB 模拟，提交后需显式开始模拟或重启。

适用于**同一广播域内不同 IP 子网**。不能跨普通路由器或隔离 VLAN；电脑仍须具有有效 IPv4。软件和固件必须同时更新，旧固件不理解新的广播回复端口行为。本轮尚未验证 W5500 实板在源 IP 为 0.0.0.0 时与 Windows 的实际双向接收，也未将软件替身的候选地址元数据视为真实跨网段验收。

实现依据：[Microsoft UDP 广播说明](https://learn.microsoft.com/en-us/dotnet/framework/network-programming/using-udp-services)、[Windows IP_UNICAST_IF / IP_PKTINFO](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options)、[W5500 UDP](https://docs.wiznet.io/Product/Chip/Ethernet/W5500/Application/udp)。

## 验证与产物

最终结果、文件 SHA-256 和范围见 `artifacts/acceptance/v0.12.11/summary.json`、`artifact-hashes.json`。原生截图保存在对应检查目录。早期失败记录保留，最终以 summary 引用的通过记录为准。

- Core 协议、音频与回环检查通过；最终记录为 `core-checks.jsonl`。
- UDP 协议与真实指定网卡广播套接字替身 44 项通过：取消、无地址元数据、异网段候选元数据、身份与事务校验、丢失 APPLY 应答后不重复执行写入。
- 原生界面回归 391 项通过。
- 独立后台界面全流程与取消专项采用正式发布的 App/Runtime/Core DLL 的相同字节运行，检查结果见 summary。包括连接发送前、HELLO、首次 GET_INFO、音频设备信息读取卡住时取消，连接失败后不继续发送、管道关闭、后台重启不重放、显式重试和取消真实 UDP 发现。
- 固件 Release 构建通过，ELF 含 V0.12.11。发布目录前台、后台 EXE/DLL 文件版本均为 0.12.11.0，实际发布前台 `--version` 返回 V0.12.11。

程序：`SoundConvertClient/artifacts/publish/v0.12.11-win-x64/SoundConvertClient.exe`，需保留完整目录及 Engine 子目录。

固件：`SoundConvertBoard/build/SoundConvertBoard.uf2`。本轮不自动烧写。

## 实板验收与回退

使用隔离测试网固定电脑网卡和板卡身份，分别验证同子网、不同子网、无 DHCP/无地址的发现和静态/DHCP 改址；核对确认提交、应答丢失、超时回滚、重启保存。再验证断网/错误 IP 连接失败、连接各阶段取消、搜索取消及显式重试，确认失败后没有继续请求或残留进度。最后独立验收 USB 模拟恢复与音频。

若相同条件下实际更差，恢复上一版完整软件与配套固件，按原配置版本兼容要求处理；保留本轮失败窗口、原设置和日志，不把构建或替身通过当作实板通过。
