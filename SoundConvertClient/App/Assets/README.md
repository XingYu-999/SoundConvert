# 应用图标与提示音

`AppIcon.svg` 为蓝底白色声波图标源文件；`tools/Generate-AppIcon.ps1` 按相同几何图形生成 PNG 预览和包含 16、20、24、32、40、48、64、128、256 像素帧的 `AppIcon.ico`。ICO 同时作为 MSBuild ApplicationIcon、Avalonia 窗口图标和托盘图标，发布后不依赖外部 ICO 文件。

## 声卡连接与断开提示音

提示音使用本机 Microsoft Huihui Desktop 中文女声合成，未使用或复制索尼耳机录音：

- `connected-zh.wav`：“声卡已连接”，管理连接成功时播放。
- `disconnected-zh.wav`：“声卡已断开”，管理连接主动断开或控制连接失效时播放，同一次断线不重复播报。

生成脚本：`tools/Generate-ConnectionAnnouncement.ps1`（Windows PowerShell 5.1 / System.Speech）。默认生成连接提示；传入 `-State Disconnected` 生成断开提示。参数为 22050 Hz、16 位单声道、语速 -1、音量 65。两个资源均嵌入程序，不依赖用户机器安装中文语音引擎。可替换为自行录制的同名 WAV 后重新构建。

两种提示共用设置中的中文女声提示开关，并通过当前选定的安全电脑输出播放。只读观察、连接未成功、停止监听、USB 音源停播及应用退出不播放断开提示；自动恢复成功不重复播放连接提示。
