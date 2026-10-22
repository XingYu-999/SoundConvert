# Wi-Fi ADB与手机UAC验收

当前V0.9.4只调试USB音频及HID按键，媒体会话配套APK、通知权限和配对服务已移除。旧Android元数据验收见文档22，仅作为历史证据。

在手机开发者选项打开无线调试，按当前显示的IP和端口明确连接；不要复用旧端口、扫描其他设备或固定改为5555。

```powershell
./tools/Connect-AndroidWifiAdb.ps1 -PairEndpoint '<手机IP:配对端口>' -ConnectEndpoint '<手机IP:调试端口>' -EvidenceDirectory 'artifacts/acceptance/android-uac/首次连接'
```

配对码由终端安全提示输入；已配对时省略PairEndpoint。若安装明确的UAC验收APK，另传-Install -Apk及实际路径；启动另需-Launch -Activity，脚本不自动授予权限。

实板验收前保存板卡身份、固件、USB预设、格式、Host和电脑耳机端点基线。手机将开发板选为USB音频输出，电脑仅向指定Realtek耳机播放。依次核对六种HID键、内部80/40/0%及静音、软断连保留和重启默认80%，区分板端接受／HID传输完成／Host真实响应。

tools/AndroidAcceptanceApp仅保留明确选择W5500 USB输入的AudioRecord RMS验收，不创建媒体会话，不读取歌曲，不保存录音。安装及麦克风授权仍由用户手动确认；权限授予不代表已测。不得使用内置麦克风替代USB输入。

当前版本编译、夹具与实板结果见文档24；Windows历史范围不推广为Android本版本验收。实板测试完成后恢复临时设置和耳机音量并关闭验收工具。
