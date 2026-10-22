# Git 忽略规则与 Windows 安装包验收

版本 **V0.12.7**，日期 2026-10-22。

根目录 `.gitignore` 增加 ToDo、docs/开发流程、各级 artifacts、.vscode、.vs、.idea、bin/obj、固件 build/build-*、CMake、Android/Gradle、本地配置、签名密钥、日志和临时文件。保留 Inno 源码、NuGet 锁文件、Gradle wrapper 和固件 vendor 源码。用 Git `check-ignore --no-index` 核验指定目录和生成目录命中规则，源码与锁文件不命中。当前 Git 元数据实际位于父目录 `D:/Github/.git`，该索引未跟踪本批要求忽略的四个目录；没有改动父仓库索引。

新增 `SoundConvertClient/InnoSetup/SoundConvertClient.iss`、打包 PowerShell、使用说明和简体中文语言文件。固定 AppId，版本读取 VERSION；当前用户／所有用户选择；同版修改修复、较新升级、阻止降级、保留原目录及配置；缓存安装包支持系统“更改”入口。前台和 Engine 运行或 WMI 检查失败时停止安装，卸载也遵守此规则。卸载配置复选框默认关闭，静默卸载始终保留配置；所有用户清理明确提示范围，并且只删除已知的配置和设备记录文件，保留日志与诊断。

Engine 项目补充 win-x64 RuntimeIdentifiers，解决前台发布过程中 Engine 的 RID 资产被非 RID 还原覆盖后出现的 NETSDK1047；首次发布核对外部依赖版本和内容哈希未改变。之后用新的发布目录验证正式 Build-Installer.ps1 的发布、编译与 SHA256 全流程，锁文件中的项目依赖版本随 VERSION 同步。

正式安装包：`SoundConvertClient/artifacts/publish/SoundConvertClient-Setup-V0.12.7-x64.exe`。软件载荷读取用户确认的 `SoundConvertClient/artifacts/publish/win-x64`，安装包放在其父目录；Inno 默认值、打包脚本及使用说明已统一。同一交付批次的路径修正，版本保持 V0.12.7。Inno 6.7.3 编译通过，完整自包含载荷打包；前台及 Engine 的 EXE、DLL 均为 `0.12.7.0`。指向旧 V0.11.10 发布目录时，编译器按预期拒绝。SHA256 见安装包旁 `.sha256`；交付摘要与各发布文件哈希见 `artifacts/acceptance/v0.12.7/installer-delivery.json`。

隔离 Inno 运行时 **15 项通过**，结果见 `artifacts/acceptance/v0.12.7/installer/results.json`。独立 AppId、测试载荷、测试进程名及专用用户配置目录，运行生产 Pascal 逻辑；测试构建用专用参数模拟卸载复选框输入，正式安装脚本无此参数。涵盖首次安装、修改入口、维护缓存、同版修复、缓存自修复、较旧版本升级、防降级、两种进程运行阻止、运行中卸载阻止、禁止维护迁移、默认保留配置卸载、保留配置重装及可选配置清理。没有使用日常配置进行破坏性卸载测试。

使用 computer-use 实际查看正式安装包：“选择安装程序模式”正确显示“仅为我安装（建议选项）”及“为所有用户安装”；简体中文语言选择正常；欢迎页显示 V0.12.7；当前用户默认路径为 `%LOCALAPPDATA%/Programs/SoundConvertClient`。仅预览向导，不将正式应用安装到用户系统。

固件仅根据同一 VERSION 离线编译通过。未刷写、未打开 COM8、未进行实板或音频验收。所有用户/UAC、跨账户会话及全 GUI 安装／卸载仍需目标机器验收，隔离回归不替代这些结果。
