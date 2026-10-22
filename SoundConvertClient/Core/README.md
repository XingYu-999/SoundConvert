# 音频接收、格式与间断处理

当前协商组合为双声道 `48000/S16LE` 和 `96000/S24LE_PACKED`。后者每样本
3 字节，有符号小端，不能按 32 位容器读取；UABA 格式码分别为 1、2。
名义 1 ms 包分别是 48 帧/192 B 和 96 帧/576 B。`GET_INFO.usb.format`
反映实际 USB Host 选中的格式，`STREAM_PREPARE` 的返回组合必须一致。
`hi_res_enabled` 只是开放能力，不能据此假定正在接收 96 kHz。

`AudioReceiver` 在后台完成协商、UDP PROBE、来源校验、解码和分发。稳定流
只发送 1 秒一次 keepalive；板端使旧代次失效后，重新查询实际格式并建立新
stream/token。另有 10 ms 本地监督：有效音频中断 40 ms 时先用轻量 keepalive
探活，成功就保留原 epoch 和缓冲；只有流已失效才重协商，收到 END 包立即
唤醒。持续静默的探活间隔至少 100 ms，不发送大体积状态轮询。首次恢复
包的实际间隔作为 `last_recovery_gap_ms`/`maximum_recovery_gap_ms` 报告。
所有收到的旧 token/epoch 包被拒绝。USB 模拟停止时等待用户
重新开始，不借监听请求隐式启动。回环回归验证 UI 同步上下文完全不泵消息时，
48/16 → 96/24 重协商仍能完成，原 `Buffer.Add` 订阅继续工作。

WASAPI 输出使用所选输出端点的实际 mix sample rate。`AudioBuffer` 的名义
步长为 `sourceRate/outputRate`，漂移校正额外限制在 ±500 ppm。48 抽头、
1024 相位 Blackman sinc 的截止频率随降采样比例变化，避免把 96 kHz 源中的
高频直接折叠到 48 kHz 输出。缓冲容量、预填充和前沿检查均按源帧计算，
输出回调时长则按输出采样率计算，不能统一除以 48。

## 本轮爆音修复

此前缺帧或 sinc 缺少抽头样本时直接填零，欠载/溢出又会立即把整个输出块
置零；这些行为可能在任意非零样值处产生阶跃。本轮保留用户设置的容量上限，
增加以下处理：

- 到播放期限才处理缺口，允许及时乱序包先补齐。≤2 ms 的已知缺口在两侧
  已知样本间插值；更长缺口用 1 ms 淡出/淡入经过静音。
- 前沿欠载不越过已收到的时间轴；重新预填充，同时用 2 ms 输出淡出/淡入
  连接边界。溢出、时间轴切换和格式变化也使用这个边界处理。
- 单纯到包延迟超过 50 ms 不再清空具有已知 sample_index 的缓冲；保留
  连续时间轴，让有界延迟/重排数据正常补齐。
- 读取也不再以“最后包超过 50 ms”强制静音。只要缓存覆盖本次输出的真实
  ASRC 消耗量及 sinc 前沿，就继续消费；不足才淡出/重缓冲。因此显式
  80 ms 容量不会被固定 50 ms 超时提前削弱，容量上限本身没有扩大。
- 本地监听增益/静音用与输出采样率相关的 5 ms 增益变化。正常音乐瞬态
  不做阈值削峰；只在明确的缺口和状态边界进行修补。
- sinc 启始历史用首个真实样本延伸，避免零填充造成虚假的前沿瞬态。

这些操作不能恢复真实丢失的声音。`MissingFrames`、`ConcealedSourceFrames`、
`ConcealedOutputFrames`、重缓冲和代次统计仍保留；没有把修补后的流宣称为
无丢样。实际 v9 请求 96 kHz、线上仍为 48 kHz 的一次 20 秒记录中，27 个
gap/1296 缺帧与板端 `usb_expired_frames` 增量 1296 完全一致，说明该批缺失
来自板端 PCM 队列过期，PC 修补不能代替板端调度修复。

## 可复现离线证据

运行：

```powershell
dotnet run --project SoundConvertClient/Checks/UsbAudioBridge.Checks.csproj -c Release
```

246 项协议、缓冲、时序与真实本机回环检查通过，包含：

- 24 位正负边界、−1 和符号扩展；拒绝按旧格式解码新包。
- 两种源格式到 44.1/48/96 kHz 输出的六种转换；1 kHz、0.5 幅度测试音
  RMS 约 0.35352～0.35355，频率约 1000.91 Hz。
- 96 → 48 kHz 时 35 kHz 输入的输出 RMS 小于 0.002。
- 注入 1 ms/3 ms 缺口及乱序：只修补预期 192/384 个源帧，最大相邻样值
  差约 0.01098/0.01112；失联、格式、静音和增益切换最大差约 0.00521。
- 48/16、96/24 两种格式预缓存 80 ms 后停包 60 ms，仍连续完成七次
  10 ms 输出，无欠载；下一次实际前沿不足才进入重缓冲。
- UI 上下文无消息泵时，实际回环 TCP/UDP 协商及短断流恢复共收到 8 个
  有效音频包，无 UI context post。70 ms 网络静默后，探活确认原流有效，
  不重协商且缓冲初始化次数仍为 1；另一次真实流失效到首个恢复包为
  78.56 ms。该数值是本机回环结果，不是板端时延保证。

这些是离线/回环证据。最终镜像的 USB 实际 96 kHz、网络连续性、WASAPI
真实输出和长测必须另行实测。

## 实机诊断入口

`tools/SourceTone` 链接生产 `WindowsAudio.cs`，指定独占 PCM 格式并报告
`IsFormatSupported`、初始化后的 `OutputWaveFormat`、Play/Stop 时间戳及异常：

24 位测试源使用 `WAVEFORMATEXTENSIBLE`、24 valid bits、L/R mask `0x3`、
PCM subformat；16 位仍使用兼容的 `WAVE_FORMAT_PCM`。这只是 Windows API
格式结构，USB 与 UABA 的 24 位 PCM 仍每样本 3 字节。运行
`SourceTone --describe-formats` 可离线检查真实封送结构及输出字段，不访问
音频设备。该选择依据 [Microsoft usbaudio.sys 文档](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/usb-audio-class-system-driver--usbaudio-sys-)
对较新 Windows 24 位 API 格式的说明。

```powershell
dotnet run --project tools/SourceTone -- "精确 USB endpoint ID" 24/96 30 --unity-host-volume
```

可选 `--unity-host-volume` 临时设 0 dB、解除静音，并在正常退出/异常时恢复
原 dB/mute；恢复失败有独立错误。强制终止进程不能保证执行 finally。
完整客户端也支持 `--usb-test-tone ID --source-format 24/96 --seconds 30`，
后者不主动修改端点音量。

`tools/AudioCapture` 按协商格式计算归一化峰值/RMS、源时长和 440/880 Hz
能量；用有界 20 ms 诊断窗口区分重排与永久 gap。通过条件包含无永久 gap、
无非法包及板端连续性计数。WASAPI loopback 的 `MonitorCapture` 额外报告
最大一阶/二阶差分、回调空窗与无效样本。

`maximum_output_step` 等差分值应与同一测试音的基线比较；正常音乐瞬态也
可能很大，不能单凭一个阈值断定爆音。`MaximumReadDurationUs` 与
`ReadDeadlineMisses` 包含初始 JIT/预热及锁等待，应结合启动阶段和稳态记录。

Windows 的上层 WASAPI 接受 96/24，不一定证明物理 USB 已采用该格式：本机
已发现一个实际拓扑连接到 Intel USB 智音 DSP 的端点把上层 96/24 转换为
板端 48/16。只有真实拓扑检测命中时才提示该情况；最终以板端 alt/format、
接收字节/帧比例和频谱记录共同判定，不自动更换用户驱动。
