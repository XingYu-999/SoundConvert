# V0.9.4 Windows 系统默认音频设备

日期：2026-10-21。本批次从当前工作区 V0.9.3 递增一次补丁版本至 V0.9.4。

## 行为

- 电脑监听输出和电脑麦克风输入的第一项均为 `Windows 系统默认（当前设备名称）`。名称来自 Windows 当前实际默认端点。
- 新配置默认选择该项；已有明确保存的具体声卡、麦克风选择保持。具体设备不可用时不静默切换至另一设备。
- 系统默认选项保存固定标识 `windows-system-default`，实际音频、格式读取和输出音量控制使用解析后的物理端点 ID。重启程序后重新解析当前 Windows 默认设备。
- 引擎约每 3 秒检查一次端点，刷新括号中的名称。选择系统默认时，默认设备变化会停止旧的相应音频链路，再按原监听、麦克风传输意图重建；手动指定的设备不会跟随系统默认变化。两方向原有格式限制继续生效。
- 默认端点不存在时显示 `无可用输出设备` 或 `无可用麦克风`，保留默认选择并禁用相应操作。默认端点是开发板自身 USB 声卡或录音端点时显示其真实名称，同时拒绝使用，避免回授。
- 程序读取 Windows 默认端点，不修改 Windows 的默认设备设置。两侧使用普通音频的 `Role.Multimedia`，不选择独立的通信设备角色。API 依据：[Microsoft GetDefaultAudioEndpoint](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint)。

## 实现范围

`WindowsDefaultAudio` 与端点记录分离持久化选择和实际设备 ID；Windows 输出、输入控制器添加默认项。`BridgeRuntime.AudioDevices` 在命令锁内统一处理列表、端点变化、格式刷新和恢复；原有输出/输入选择、音量写入、提示音与实际音频创建使用解析后的端点。IPC 快照携带默认项名称及解析结果，前台现有绑定显示更新。

本需求没有修改固件音频或协议代码，固件按项目当前源码和根目录 `VERSION` 同步构建。工作区其他批次修改沿用其各自验收记录。

## 已完成验证

证据目录：`artifacts/acceptance/v0.9.4-default-audio`。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 固件、.NET Release 构建 | 通过；客户端 0 警告、0 错误 | `build-all.log` |
| 协议与音频离线检查 | 850 项通过 | `build-all.log` 最后一条 JSON |
| 默认设备回归 | 27 项通过 | `defaults/result.json`、`default-checks.log` |
| Avalonia 原生界面回归 | 205 项通过 | `ui/result.json` |
| 输出选项显示 | 已检查截图，名称和括号完整 | `ui/00a-default-output.png` |
| 麦克风选项显示 | 已检查截图，名称和括号完整 | `ui/00b-default-input.png` |
| Windows x64 自包含发布 | 通过，包含配套 Engine | `publish-final.log` |
| 发布版本和文件版本 | V0.9.4 / 0.9.4.0 | `published-version.json`、`release-versions.json` |

默认设备回归使用注入端点模拟两方向切换、暂时缺失、重新出现和板端误选，验证实际 ID 读取、输出音量写入、旧采集释放、故障后保留恢复意图、保存重开、旧配置兼容、定时刷新及 IPC 序列化。末尾额外只读核对真实 Windows API：当时的输出默认项为 `耳机 (Realtek(R) Audio)`，输入默认项为 `Virtual Mic (Virtual Mic for AudioRelay)`；这是测试时的快照，程序运行时按当前系统配置显示。

首次发布检查发现增量构建复用了旧版 EXE 启动器资源，DLL 和运行时版本已经为 V0.9.4。删除两处中间 `apphost.exe` 后，用本机缓存的 .NET 10.0.12 模板显式指定 `AppHostSourcePath` 重新生成并发布；最终前台、Engine 的 EXE 与 DLL 文件版本均核对为 0.9.4.0，版本命令实际返回 V0.9.4。

新增检查使用隔离配置。未更改真实系统默认设备、端点音量或静音状态，没有采集或播放真实音频。动态切换的音频链路使用注入设备验证；本任务没有实板烧写、COM8 联调或真实播放/麦克风切换验收。编译与上述替身检查不代表实板结果。

## 交付

运行 `artifacts/releases/V0.9.4/client-win-x64/SoundConvertClient.exe`，保留同目录文件和 `Engine` 子目录。新旧前台、后台应一同升级。已有具体设备选择的用户可在两处下拉框中手动选择 `Windows 系统默认（…）`。

固件同步产物位于 `artifacts/releases/V0.9.4/firmware`，本任务未执行烧写。
