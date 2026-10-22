# UDP 时间同步实施与实机验证

> 2026-10-22 当前规则更新：V0.11.3 起不允许退出软件后继续播放。托盘和设置统一退出应用，关闭到托盘仍保留进程；正常退出停流并结束后台，UI 被强制结束时回收所属后台。本文旧的退出后连续播放目标／结果保留作历史记录，不再作为当前验收要求，见[文档35](35-退出应用停止播放与后台回收验收.md)。

日期：2026-10-21 起。开发批次 V0.11.0（仅递增一次），默认 **Legacy**。对应[开发计划](../ToDo/UDP音频时间同步与低延迟开发计划.md)及[验证与收益门槛](30-UDP音频时间同步验证与收益判定.md)。本文按实际证据更新，未执行项目不预填通过。

用户指定的第一阶段已完成：V0.10.1 的完整短时双向/500 正常控制/40 突发/10 次 UI 崩溃组合及三次发布短窗通过，见[文档31](31-V0.10.1短时复验与后续基线.md)。该调度、设备名称缓存和起播余量修复的收益不归给时间同步。A0 包 SHA256：`7948837F30564D4574E23ABAB1220F267F7FD910226AC65F6E1348C1E3FC6677`，614 文件校验通过，完整目录 `artifacts/releases/V0.10.1` 不再重建。

## 实现与默认行为

- 板端通过 owner-only `SET_CLOCK_OBSERVATION` 协商 `clock_observation_v1`，绑定 boot/stream/有效 epoch/token。104B UABT 与 UABA 分流，复用原 socket，单接收者；不新增 W5500 socket 或第三个 TCP 观察连接。协议详见文档03。
- 单未完成交换，整个 P1→P4 250ms 期限；2Hz 获取、1Hz 合格观测，无追补。请求 ID 在同身份重绑定时保持单调。时间服务超时后仅有界重绑定 clock，不重连健康音频。
- 板端最多4Hz、每网络循环一个请求、排队2ms、音频优先。CLOCK completion 独立记账；接收/发送服务包含 SPI，100µs 超限撤销并按身份锁存，`RESET_STATS` 不清锁。UABA/FEC、UAC1、原4ms期限保持。
- USB 提取完成后在 IRQ 临界区外取64位单调时间，通过原 SPSC 队列发布一致锚点。该采点不等于硬件精确采集时间。
- 中心化拟合 B/P、S/B 和实际输出 O/P，整型先减原点再转浮点。32独立样本、至少30s、60s窗、残差p95≤500µs及10/30/60s频差一致性≤20ppm；不合格不强行启用。3s Holdover、8s Fallback；辅助控制可更早保守退出。
- 输出观测使用实际渲染流的 `IAudioClock`，独立 MTA 线程10Hz，不在渲染/接收/控制/快照锁内调用 COM。保留原始 HRESULT、位置、频率、配套100ns QPC及查询区间，拒绝 S_FALSE/停滞/单位或原点异常。停止超时可观察失败，先停音频并安全延迟释放，不强行中止 native 调用。
- Legacy 不启动时间任务；ObserveOnly 沿用原 PI/ASRC。实验辅助仅三观察器合格且健康时接入，±500ppm、≤100ppm/s，不跳样、不隐式改 cap/prefill。身份/端点代次、硬错误及健康数据过期及时否决，模型与 JSON 发布仍1Hz。板健康采样须字段完整且年龄≤3.5s，长监视周期可能保守回退。
- 验收可显式选择 `SidebandOnly / BoardMapping / Full`，默认 Full；分别对应 B2/B3/B4。前两级不读取输出 clock，也不启用辅助。用户界面仍为三种模式，变更后显式应用重启输出。

固定官方 NAudio 2.2.1 WasapiOut 源码保留渲染算法并增加显式 client/clock 适配，附 MIT LICENSE/NOTICE。依据：[官方 WasapiOut](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/WasapiOut.cs)、[Microsoft GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition)。QPC 原点保护的1ms未来容差、100ms陈旧上限只作合理性检查，不表示拟合或绝对同步精度。

## 已执行软件检查

证据根：`artifacts/acceptance/clock-sync/V0.11.0`。

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| ClockSyncChecks core-03 | 7290 PASS | C/C#黄金、严格包校验、独立四时钟、20 frozen seeds、偏移/漂移/不对称、连续3h两次32位回绕、失效及闭环交接；纯离线 |
| OutputClockChecks offline-04-render-timing | 112426 PASS | 原始单位/代次/原点/停止超时/延迟释放及渲染缓存并发 fake 检查，缓存分配0；不代替真实 COM |
| ClockTransportChecks transport-cadence-01 | 143 PASS | 原120项真实本机TCP/UDP门槛＋23项周期检查；旧端兼容、非法/迟到包、同身份单调重绑定、不重建音频；合成对端 |
| collector-03 / selfcheck-stop-audio-standard-01 | 26 / 39 PASS | 有界队列、去重/缺口、写盘失败、严格降级、真实回复与预算锁存；新增250ms停止文件及可选首错退出。无硬件 |
| 板端宿主 | clock564、RX281、capture wrapper18、Mic2204、USB24282、SPI构帧53678及原回归 PASS | 纯逻辑/USB替身，不代替 SPI/跨核实板 |
| 原 Core | 1213 PASS | 原协议与音频回归；到包回环相关检查数可随实际采样数变化 |
| 原生 UI / EngineUI / Lifecycle | 297 / 628 / 98 PASS | 注入对端、真实进程；滑块最大31.9238ms，原150ms门槛不变 |
| 默认端点 / 策略 | 29 / 17 PASS | 只读真实 API 与注入策略 |
| 固件 / 客户端 / 发布目录 | 构建成功 | 版本、实际运行读回和发布冒烟另记 |

`transport-01` 的120项运输门槛通过，但最终证据序列化错误，保留原失败；修复后 `transport-02` 才记整体通过。core-01 的单位检查失败、证据写盘故障注入的预期 FAIL 也不删除或改写。

## 当前主机与实板

THINKBOOK-XING，Windows USB Host，`192.168.8.74 → 192.168.8.73`，DeviceId `aa36483cef4256f169f4f4e5322c6df8`，USB serial `E53B126BB2E31A4A`。耳机和真实外部麦克风与 A0 相同，48k/16 PCM、FEC/DSP关闭、40ms、3s监视。每次隔离设置并恢复耳机音量/静音；未改默认端点/NIC配置。

V0.11.0 首次刷写 ELF SHA256 `93358AFC32301E4D7ABE3F6B639F6723AF269F8FAFBA38A4D413AC91DC450B67`。SWD Verify 与 COM8 115200/8N1 的实际 `V0.11.0` / DeviceId / DHCP地址读回分别保留；写入验证不当作音频通过。

| 窗口 | 结论 |
| --- | --- |
| new-client-old-firmware-legacy-01 | 新0.11客户端默认Legacy＋旧0.10.1固件，15s双向 PASS13 |
| new-client-old-firmware-01 | 音频15s连续、输出clock210有效，但通用工具因无exchange判FAIL；保留原记录 |
| new-client-old-firmware-02 | 严格旧固件降级场景 PASS13：UABT请求0、控制器Inactive、199输出样本完整；完整三时钟标NOT_RUN/unsupported |
| old-client-new-firmware-01 | 冻结0.10.1客户端/runner＋0.11固件，15s双向 PASS12；未协商时全部clock计数0 |
| final-new-client-old-firmware-01 | 最终发布客户端＋冻结0.10.1固件，15s严格降级PASS13；能力不足无UABT、辅助不启用，完整三时钟标NOT_RUN/unsupported |
| final-old-client-new-firmware-01 | 冻结0.10.1客户端/runner＋最终D1B60固件，15s双向PASS12；已恢复最终0.11固件并保留SWD／COM8读回 |
| B1/legacy-published-final-01 | 最终发布客户端默认Legacy，30s PASS13；exchange/reply/output-clock记录均0，正常关闭 |
| B1/legacy-short-01 | FAIL：30s capture underruns +1，missing/late/queue drop/源过期均0；PC输出与板播放六项均0，clock活动全0。累计control/poll峰值起止相同，不能归到本窗；后续已补callback供给与板FIFO事件的最小时间证据，根因仍未闭环，不通过放宽Gate处理。 |
| B1/legacy-diagnostic-02 | 60s PASS13：全部音频门槛增量0，fallback补偿帧增量0，非空callback最大12.7225ms；未复现并不等于根因修复 |
| B1/legacy-diagnostic-no-metrics-03 | 60s PASS13：关闭周期IPC采样，全部音频门槛增量0；非空callback最大10.9512ms。单对短窗不能证明IPC因果 |
| B2/sideband-short-01～05 | FAIL：先后触发完整服务100µs预算锁存，峰值231/124/103/110/105µs。所有失败原目录及相应固件保留；01的旧工具ok不代表CLOCK阶段通过 |
| B2/sideband-short-06 | 30s PASS13：CLOCK最大89µs、无预算锁存；8个真实回复/46条交换，音频错误增量0。低回复率仍须独立验证模型合格，不能只看本项ok |
| B2/sideband-short-07 | FAIL：SEND grace消除busy，但RX峰值101µs锁存；19回复/50交换。新诊断与反汇编定位到RAM函数仍经veneer调用Flash的memset／memcpy，后续候选已消除该冷路径；不放宽预算 |
| B2/sideband-short-08 | 30s PASS13：消除三处Flash内存调用后最大82µs、无锁存、24回复/66交换，音频错误增量0；仍须通过拟合阶段 |
| B2/sideband-short-09 | capture后机会、G100，30s PASS13、最大85µs；25回复/66交换，正式窗busy0但36请求过期，回复锚点年龄中位495µs |
| B2/sideband-short-10-g40 | 独立G40，30s PASS13、最大88µs、无busy/锁存；36回复/66交换。未把单次回复率改善当统计或稳定性证明 |
| B2/sideband-short-11-rx-priority | 先处理已排队音频的RX准入，30s PASS13、最大88µs、无busy/锁存，42回复/66交换 |
| B3/mapping-short-01 | 音频120s PASS13，但D03 FAIL：25回复/171交换，模型合格快照0，MappingId增长至6；8s空洞反复清窗。单列clock-stage-verdict.json，不能把runner的ok当拟合通过 |
| B3/mapping-short-02 | 音频120s PASS13，但D03仍FAIL：29回复/171交换，合格快照0；仅增加自然服务点和anchor年龄条件不足，刚提交PCM的首次busy会消耗该anchor唯一尝试。保留此候选及失败 |
| B3/mapping-short-03 | 120s音频PASS13；93回复/244交换，4/25快照Board合格，最大87µs。曾因空洞重新分段，末尾36点／42.15s、p95 152.70µs、频差10.09ppm。达到最低可用性，不是持续合格证明 |
| B4/full-short-01 | 120s音频PASS13、1149输出样本有效；三时钟5/25同时合格，末尾因回复空洞失格并重分段，81回复/219交换。最大89µs、未锁存；1Hz稳态下回复率不足以持续保留32点，继续调度复验 |
| B4/full-short-02-g40 | 180s音频PASS13，但完整三合格筛选FAIL：输出原始position/QPC呈毫秒级量化。1707条native样本HResult均0，其中806条(47.22%)位置在10ms边界；独立重算末60s残差p95约2946µs，旧Full01同算法56.20µs，门槛未变 |
| B1/legacy-combined-final-01 | PASS52：180s无界面、10次UI崩溃重开、正常500/500完成、突发39完成＋1有界pending拒绝；HID539/539、drop0，音频连续性增量0。正常p95 32.4262ms/p99 58.7925ms；历史capture underruns 1→1非新故障 |
| B4/observe-combined-final-01 | 音频／控制PASS52：540/540完成，HID540/540/drop0；正常p95 30.7859ms/p99 61.1269ms。输出时钟0/69合格，三同时0，D04最低可用性FAIL，不能将音频通过当时钟质量通过 |
| B5/assist-gating-short-01 | 60s音频PASS13：三合格5/14、最近一次控制Update的Active状态6/14，确有接入及FallbackCount增长，仅PASS/minimum_availability；实际收益未证 |
| endurance/observe-2h-01 | 实测7200.10685s、PASS13、正常关闭。ObserveOnly／40ms／3s监视／5s诊断IPC／counters-only，16项音频增量0；5392回复、4823超时、66086输出样本、1434快照，77737条证据全部写入、缺口0、锁存0、最大92µs。时钟资格与资源结论独立见摘要 |

原始 run 结果中的 `ok` 仅覆盖所选音频与证据完整性门槛。是否拟合合格、板端100µs预算、资源非退化、实验辅助是否实际接入，分别核对，不能从 `ok` 自动推导。

B1 失败说明此前短窗并不能保证反向通路在更长窗口持续无故障。`capture.underruns` 的原条件是 FIFO 不足两包且源队列空，不要求 sampleIndex 缺口。旧 `concealed_frames` 主要统计协议缺口补偿，fallback 静音未计入该字段；其为0不能排除静音。新增诊断保持24ms目标和原算法，另计 fallback 实际提交帧数，并记录 callback/非空供给间隔、欠载时刻/FIFO/入队年龄/服务间隔。新诊断不等于已修复根因。

B2 的修复只限于已协商的 CLOCK 路径：有界UDP头/负载读取、RAM执行、精确目的地址缓存、合并相邻完成寄存器，以及一次全双工SPI发送地址头与数据。复用SDK原有有界FIFO实现，SPI仍25MHz，原PCM/FEC发送路径及Legacy回调保持原实现。100µs、2ms队列及4ms音频期限未放宽；超限按完整身份锁存，不能靠重新启用同身份清除失败。

后续调度候选仅在现有音频服务后的自然空隙尝试；同一真实锚点最多一次SPI preflight，USB idle时每主循环最多一次。窗口收窄为提取后200–500µs；普通发送距SEND提交不足grace时仅检查内存并让出、不占slot、不访问SPI、不sleep。此前100µs grace候选与 `B2/sideband-short-09` 原始证据保留：正式窗60accepted／24submitted／36expired、busy=0，说明未证明稳定1Hz下的持续资格。

2026-10-22另建独立40µs grace候选，仅改变这一内部尝试时机参数，不试32µs、不同时改变其它策略。grace是避免过早浪费slot的启发式，不是完整RX/TX服务100µs安全预算；40µs不代表SENDOK保证，仍须查询实际完成，busy仍耗该锚点唯一机会，超过原2ms期限照常作废。500µs资格上限、音频4ms、每锚点一次和所有拟合门槛保持不变。先30秒Sideband筛选，再Full至少180秒覆盖2Hz→1Hz后的完整60秒窗口；未执行的结果仍为待测。诊断分别记录等待、真正preflight和busy时的kind／提交年龄，避免把USB提取年龄误当作发送完成年龄。

`69CCE85284E1C97FEE5FBD2F3958E243F6D6688620F98E24F26267B833F863FB` ELF进一步使用独立115B零尾read-TX缓冲、固定上限的RAM复制／清零，避免编译器重新生成Flash memcpy／memset。`clock-no-xip-memory-elf.json`记录6个目标函数／克隆均在RAM且无这些内存调用；不是只凭源码attribute断言完整热路径已在RAM。总固定scratch增加115B，未改SPI SDK或引入DMA。

RX准入候选ELF `D1B60CA8AB74DBE7AC79A6E49A7E436913D3D30CF5DE630A25567D59BE32692D`：只有CLOCK已启用时，若软件待发音频／FEC或USB队列非空，本次不查RX、不推进RX_RD，先执行原flush／drain，下一自然机会再读。硬件completion.pending不参与此准入，防止永久饥饿；Legacy／初始PROBE仍原路。该优先性针对检查瞬间，不声称跨核原子实时保证。

Full02与Full01使用相同实际端点、48k float32客户端、1056帧／22ms输出及相同Runtime/Core。原始配套QPC年龄p95仅2.1µs、native查询p95 58.9µs，故量化不能归为查询晚了数毫秒或拟合巨大原点误差。原有Buffer.Read耗时亦不足以证明WASAPI持续有padding；当前证据不能区分驱动时钟配对质量与写入末端截断／render starvation。保持500µs／20ppm门槛，补充既有padding／成功release缓存诊断后复验，不能丢掉边界样本强行合格。

渲染诊断采用原有padding／ReleaseBuffer调用的原始事实，不新增COM查询。单写者缓存、读端最多3次version一致检查，否则Unknown；RT无新增锁／线程／分配。独立fake检查112426项通过，100000次缓存写读测得分配0；该检查不代替真实COM。

新诊断的真实组合窗完整3160条／344.24s原始时钟中，1437条边界样本全部精确满足 `Position×SampleRate = (SuccessfulReleaseFrames−BufferFrames)×Frequency`。buffer=1056帧，已有padding读数576帧，稳态zero-padding0，最大steady release间隔14.2552ms；没有本窗渲染队列耗尽证据。这是“最近提交减整块buffer”的游标平台，不能称为卡在最后写帧。末60s残差p95 3022.09µs，仍正确失格；不能推断此端点永久不支持，其他窗口曾合格。完整／子集分析各自保留于candidate-01/output-clock-render-correlation-02及-01（JSON+MD）。

B5诊断时间层必须区分：Qualified／Requested是新资格输入；Active／Applied是最近一次有效漂移控制Update状态。1Hz发布紧接SetInput、而静音／缓冲／包龄≥3ms可跳过Update，故会见到true/false或false/true组合。AudioBuffer同一锁串行输入与Update；资格撤销后的下一次eligible Update先将当前总量交接给原PI，不调用UpdateWithFeedforward。5s采样和FallbackCount增长不能证明交接时延上界，亦不能仅因这类组合断言越权。离线控制轨迹与实际B5样本分开保存。

长测选择仅观测，是在音频／控制短测及实际接入／降级短测后验证工作线程、连续性与资源；没有强行启用辅助。5s诊断IPC在场，不能将本窗描述成“无任何IPC客户端”；counters-only没有保存长时WAV，不能宣称波形或模拟质量通过。主机睡眠策略只读核对AC/DC均Never，未改电源计划。窗口自UTC 2026-10-21 16:40:28.5714264开始，实测7200.10685s，完整结束后才记PASS；通过两次32位微秒回绕及输出position跨有符号32位边界，采样与身份连续性另在长测摘要列明。

长测派生报告 `endurance/observe-2h-01/summary-extra.json` 与 `clock-stage-measurement-01.json` 已冻结，原result未改。三观察器同时合格814/1434（56.7643%），分别为Board821、Source1026、Output1429；另620点未同时合格，MappingId从1到13（12次变化），末尾Board／Source失格。因此仅达到最低可用性筛选，D03持续资格与完整发布资格仍未通过。ObserveOnly的Active在1434点均为0，Engine身份、源代次38及同窗boot保持一致。

正式长窗内16项守护及额外late／reorder overflow／fallback／source overrun／board dropped增量均0；非致命reordered单列+4225。历史USB expired768→768、capture underruns4→4、fallback frames131616→131616，不能把起点已有计数写成全程为0。板端sampled_at_us64从1508539963到8708639571，实跨2^32和2^33；原生输出位置30936→2767162576（384000Hz）实跨有符号32位边界，均无倒退。

资源按第二小时716点描述：Engine CPU均值为一个逻辑核的82.5535%（约0.8255核），不是整机CPU百分比。Working Set均值223.595MiB，首184.676→末194.043MiB，回归斜率+36.687MiB/h；Private均值181.917MiB，首143.223→末150.828MiB，回归斜率+38.222MiB/h。曲线有涨落，单窗既不证明泄漏也不证明资源门槛通过；没有同条件配对基线，不能将其归因于时间同步。当前1Hz完整诊断JSON仍有固定大对象分配，按需在观察器外生成的优化尚未实现，见 `candidate-01/clock-diagnostics-allocation-review.md`。

最终输入复核 `candidate-01/input-recheck-after-endurance-01.json`：650条文件哈希记录、647个唯一文件均一致，0缺失／0不稳定；原输入清单未改。A0整包614文件复核通过后才做最终兼容刷写，未修改其内容。恢复的最终ELF仍为D1B60，兼容复验时的重启与长测boot分别记录，不能把复验后的新uptime误当长测未满2小时。

Core1实际由 `multicore_launch_core1_with_stack` 使用既有16KiB显式栈，ELF中的SDK 2KiB默认栈符号未用于该启动。静态反汇编与深度上限估算最长JSON／audio／clock链约7KiB量级，不是动态high-water实测；没有为通过本候选新增栈容量。

PC请求节拍另作独立修复：以P1为周期起点扣除已花费的交换时间，250ms timeout不再额外叠加到500／1000ms周期；若整周期超时则从当前另等完整周期，禁止追补突发。23项确定性周期检查及完整143项回环通过，统一重新发布的客户端／Engine版本均0.11.0.0，实际发布冒烟10项通过。先前客户端完整副本保留于candidate-01/pre-p1-cadence。

## 结论边界

本候选在选定Windows条件下的2小时音频连续性与证据完整性通过；尚未证明相较原控制器降低物理耳机延迟、提高长期稳定性或具备可发布的辅助收益。默认保持 Legacy。数字 loopback、样本计数、拟合残差和软件驻留不替代模拟输出录音/人工盲听；网络不对称使绝对单向延迟仍未知。完整10min配对、30min确认、2h/8h矩阵及其他Host范围按实际完成状态另记。

## 候选交付与复核

候选目录 `artifacts/releases/V0.11.0`，压缩包 `artifacts/releases/USB-Audio-Bridge-V0.11.0-candidate-win-x64.zip`，同名 `.sha256` 为整包校验。包内包含完整Windows客户端／独立Engine、匹配ELF／UF2、文档、剩余清单、复验工具、第三方许可及选定通过／失败证据；原始大体量日志仍保留在仓库。运行程序自包含，附带的验收工具另需.NET 10 Windows环境。

包内 `manifest.json` 逐文件记录长度和SHA256，`source-manifest.json`保留长测固定源码／运行输入；使用 `tools/Verify-SoftwareRelease.ps1 -Root <解压目录>` 复核。该交付标为候选，完整发布验收为NOT_PROVEN。可用Git根目录缺失，未提供commit或声称工作树干净，以固定输入与文件哈希追溯。
