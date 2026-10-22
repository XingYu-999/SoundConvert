# Windows 安装包

`SoundConvertClient.iss` 参考 FdrSoftware 的维护／升级与卸载选项，以及 VirtualDriverControl 的安装范围选择。仅包含本项目所需行为，没有引入数据库、驱动安装、管理员启动或其他项目的配置。

## 编译

使用 Inno Setup **6.7.3 或更高版本**，从已发布的软件目录打包，在仓库根目录运行：

```powershell
.\SoundConvertClient\InnoSetup\Build-Installer.ps1 -SkipPublish
```

脚本读取根目录 `VERSION`。默认软件发布目录为 `SoundConvertClient/artifacts/publish/win-x64`，安装包输出到其父目录 `SoundConvertClient/artifacts/publish`，与当前 Visual Studio 文件夹发布配置一致。直接用 Inno IDE 打开 `.iss` 也使用这组默认路径。

如果需要同时发布前台和 Engine，安装 .NET 10 SDK，并省略 `-SkipPublish`。此时发布目录必须为空或尚未创建；已有发布目录请使用 `-SkipPublish` 复用，或用 `-PublishDirectory` 指定一个尚未创建的目录。脚本不会自动清空已有目录。

```powershell
# 复用已经核对的自包含发布目录
.\SoundConvertClient\InnoSetup\Build-Installer.ps1 -SkipPublish

# 指定发布目录和编译器
.\SoundConvertClient\InnoSetup\Build-Installer.ps1 -SkipPublish `
  -PublishDirectory 'D:\Temp\SoundConvertClient' `
  -CompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

默认输出为 `SoundConvertClient/artifacts/publish/SoundConvertClient-Setup-V<版本>-x64.exe` 及其 `.sha256`。指定其他 `-PublishDirectory` 时默认输出到该目录的父目录，也可用 `-OutputDirectory` 指定输出位置。编译时校验前台和 Engine 的 EXE、DLL 四个文件版本，全部必须等于 `VERSION + .0`，并检查双方自包含运行库及第三方许可文件。源码包含简体中文语言文件，保留其原始署名。

## 安装、修改与升级

- 桌面快捷方式和开始菜单文件夹、启动条目统一命名为“USB 音频桥接工具”，卸载条目为“卸载USB 音频桥接工具”。升级不沿用旧英文菜单文件夹；仅清理确实指向本安装程序／卸载器的已知旧英文快捷方式，旧文件夹为空时才移除。
- 首次启动显示“仅为我安装／为所有用户安装”。默认仅当前用户，安装到当前用户 Programs；所有用户模式使用 Program Files 并请求管理员权限。使用 `{autopf}`、`{autodesktop}` 等范围感知目录。升级默认沿用原范围和目录；命令行可用 `/CURRENTUSER` 或 `/ALLUSERS` 明确指定范围。依据见 [Inno 安装范围说明](https://jrsoftware.org/ishelp/topic_setup_privilegesrequiredoverridesallowed.htm) 与[沿用原范围](https://jrsoftware.org/ishelp/topic_setup_usepreviousprivileges.htm)。
- 同一版本重新运行是修改／修复，可重新安装程序文件和调整桌面快捷方式。新版本重新运行是升级；保留 `%LOCALAPPDATA%\UsbAudioBridge` 中的设置、设备记录与备份，较低版本被阻止。固定 AppId，后续版本不能更换。
- 系统“程序和功能”的“更改”入口调用 `{app}\Maintenance\SoundConvertClientSetup.exe`。该目录缓存完整安装包，会占用约一个安装包大小的额外空间。修复时跳过覆盖正在运行的自身缓存，升级时更新缓存；卸载时由 Inno 安装日志清理。
- `/MODIFY`、`/UPDATE` 要求当前安装范围内已有安装记录。修改和升级锁定原安装目录，避免 `/DIR` 无意创建第二份程序；迁移目录需先卸载并保留配置，再安装。
- 安装入口、准备写入与实际安装前均检查前台和 Engine。托盘及其他登录会话中的同名进程也阻止安装；普通、静默安装规则相同。WMI 运行状态检查失败时停止操作。请通过托盘“退出”结束软件后重试。

静默升级示例，按原安装范围选择参数：

```powershell
.\SoundConvertClient\artifacts\publish\SoundConvertClient-Setup-V0.12.10-x64.exe `
  /UPDATE /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

安装包为 win-x64 自包含文件夹发布，不需要另装 .NET 运行时。源码中的 Windows 版本门槛不代表所有系统版本均已验收；.NET 官方支持范围见 [Microsoft Windows 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。设备发现和未连接 IP 配置使用 UDP 广播，无需额外驱动。

## 卸载配置

交互卸载显示默认不勾选的“删除配置”选项：仅当前用户安装清理当前用户；所有用户安装明确提示将清理本机各用户标准配置目录。后者从 Windows ProfileList 读取本地／域／Azure AD 用户配置路径，不删除其他软件数据。

选中时只删除 `settings.json`、`settings.json.bak`、`settings.json.tmp`、`settings.schema1.bak`、`devices.json`、`devices.json.tmp`。日志与诊断保留，不递归删除用户目录。特殊重定向的非标准 LocalAppData 目录需由对应用户自行清理。

静默卸载始终保留配置。卸载也要求前台和 Engine 均已退出；只清理当前卸载用户、且确实指向本安装 EXE 的 `UsbAudioBridge` 开机启动项，其他用户离线注册表中的自启动值不作修改。

## 中文快捷方式更新（V0.12.10 / 2026-10-22）

桌面及开始菜单名称统一为“USB 音频桥接工具”，升级旧英文快捷方式的隔离 Inno 回归 9 项通过。前台／Engine 文件版本同步为 0.12.10.0；新安装包仍位于 `SoundConvertClient/artifacts/publish`。完整记录见 `docs/开发流程/52-安装快捷方式中文命名验收.md`。

## 初始安装器验证（V0.12.7 / 2026-10-22）

- 正式安装包编译通过；前台／Engine 四个 EXE、DLL 均为 `0.12.7.0`。旧 `V0.11.10` 发布目录已被编译版本校验拒绝。
- `Build-Installer.ps1` 已使用新的发布目录完成“发布前台及 Engine → 编译安装包 → 输出 SHA256”全流程。交付摘要位于 `artifacts/acceptance/v0.12.7/installer-delivery.json`；Git 忽略规则另有 15 项排除、7 项源码保留检查通过。
- 按用户确认的实际发布目录调整默认路径后，已从 `SoundConvertClient/artifacts/publish/win-x64` 重新打包成功，安装包与 SHA256 位于其父目录；Inno 直接编译默认路径检查通过。此为同批路径修正，版本仍为 V0.12.7。
- 已实际预览正式安装包：当前用户／所有用户选择显示正常，简体中文与 V0.12.7 欢迎页正常，当前用户默认路径为 `%LOCALAPPDATA%/Programs/SoundConvertClient`。预览后退出，没有将正式程序安装到系统。
- 隔离 Inno 运行时回归 **15 项通过**：首次安装、修改注册项、缓存维护、同版修复、升级保留设置、防降级、前后台运行阻止、卸载阻止、禁止维护迁移、静默卸载保留设置、重装及可选配置清理。
- 回归使用独立 AppId、测试载荷／进程和专用配置目录，直接执行生产 Pascal 安装／卸载逻辑；测试构建用独立参数模拟卸载复选框输入。正式脚本不含该测试参数。原始测试脚本、日志与 `results.json` 位于忽略上传的 `artifacts/acceptance/v0.12.7/installer` 及 `artifacts/Test-InnoInstaller.ps1`。
- 固件按同一 VERSION 离线编译通过；没有刷写或操作 COM8。隔离回归不代表所有用户安装的 UAC、其他登录会话、生产配置删除、实板音频或完整安装 GUI 矩阵已经验收。
