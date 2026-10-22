# V0.11.9 移除 Host 控制详情

删除共享 `HostMediaPanel` 中“Host 控制详情”折叠区及其中的固件能力说明和 HID 待处理／完成／丢弃计数显示。快捷控制区域保留六个图标按钮、可用性提示和本次按键操作反馈，删除后不保留空白占位。

本次仅改界面。设备连接、HID 命令和后台状态采集保持原有实现，固件仅同步版本为 V0.11.9。

证据位于 `artifacts/acceptance/v0.11.9`，界面回归、构建及交付校验最终结果见 `software-delivery.json`。使用现有原生界面检查及截图核对播放页；本轮未进行固件烧写、COM8 串口联调或实板音频验收。

退出旧版后运行 `artifacts/releases/V0.11.9/client-win-x64/SoundConvertClient.exe`，完整保留 Engine 子目录。
