#if Ver < EncodeVer(6, 7, 3)
  #error "Please use Inno Setup 6.7.3 or newer."
#endif

; VERSION is the only version source. Do not override it with a compiler define.
#define VersionHandle FileOpen(AddBackslash(SourcePath) + "..\..\VERSION")
#if !VersionHandle
  #error "Cannot read the repository VERSION file."
#endif
#define MyAppVersion Trim(FileRead(VersionHandle))
#expr FileClose(VersionHandle)
#define MyAppName "SoundConvertClient"
#define MyShortcutName "USB 音频桥接工具"
#define MyAppId "E6F57A8B-358D-4EA4-BFC6-C5BA6AA761D3"
#ifndef MyPublishDir
  #define MyPublishDir AddBackslash(SourcePath) + "..\artifacts\publish\win-x64"
#endif
#ifndef MyOutputDir
  #define MyOutputDir ExtractFileDir(MyPublishDir)
#endif
#ifndef MyCompression
  #define MyCompression "lzma2"
#endif
#if !FileExists(MyPublishDir + "\SoundConvertClient.exe") || !FileExists(MyPublishDir + "\SoundConvertClient.dll") || !FileExists(MyPublishDir + "\Engine\SoundConvertEngine.exe") || !FileExists(MyPublishDir + "\Engine\SoundConvertEngine.dll")
  #error "Publish the complete client and Engine before compiling this installer."
#endif
#if GetFileVersion(MyPublishDir + "\SoundConvertClient.exe") != MyAppVersion + ".0" || GetFileVersion(MyPublishDir + "\SoundConvertClient.dll") != MyAppVersion + ".0" || GetFileVersion(MyPublishDir + "\Engine\SoundConvertEngine.exe") != MyAppVersion + ".0" || GetFileVersion(MyPublishDir + "\Engine\SoundConvertEngine.dll") != MyAppVersion + ".0"
  #error "Client / Engine EXE and DLL versions must match the repository VERSION."
#endif
#if !FileExists(MyPublishDir + "\coreclr.dll") || !FileExists(MyPublishDir + "\hostfxr.dll") || !FileExists(MyPublishDir + "\Engine\coreclr.dll") || !FileExists(MyPublishDir + "\Engine\hostfxr.dll")
  #error "Both client and Engine must be self-contained folder publications."
#endif
#if !FileExists(MyPublishDir + "\NAudio-WasapiOut.LICENSE.txt") || !FileExists(MyPublishDir + "\NAudio-WasapiOut.NOTICE.md") || !FileExists(MyPublishDir + "\Notices\ControllerForwardingTool-MIT.txt")
  #error "The published license / notice files are missing."
#endif

[Setup]
; Fixed identity: keep this GUID for all future versions and maintenance runs.
AppId={{{#MyAppId}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} V{#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
DefaultDirName={autopf}\SoundConvertClient
DefaultGroupName={#MyShortcutName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableWelcomePage=no
UsePreviousAppDir=yes
; Apply the Chinese group name when upgrading an older English installation.
UsePreviousGroup=no
UsePreviousTasks=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=yes
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
SetupIconFile=..\App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\SoundConvertClient.exe
AppModifyPath="{app}\Maintenance\SoundConvertClientSetup.exe" /MODIFY {code:InstallScopeParameter}
OutputDir={#MyOutputDir}
OutputBaseFilename=SoundConvertClient-Setup-V{#MyAppVersion}-x64
Compression={#MyCompression}
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallLogging=yes
SetupMutex=SoundConvertClient.Setup
; Running programs must be exited by the user, including silent installation.
CloseApplications=no
RestartApplications=no
AlwaysRestart=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; Keep native libraries, .NET runtimes, Engine and notices. Never ship user data.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.log,*.tmp,*.bak,settings.json,devices.json,events.jsonl*,\diagnostics,\logs,\Maintenance"; Flags: ignoreversion recursesubdirs createallsubdirs
; Cache the complete installer for the Windows Modify action. Self-repair skips
; overwriting its own running package; the original uninstall log still owns it.
Source: "{srcexe}"; DestDir: "{app}\Maintenance"; DestName: "SoundConvertClientSetup.exe"; Flags: external ignoreversion; Check: ShouldCacheSetup

[Icons]
Name: "{group}\{#MyShortcutName}"; Filename: "{app}\SoundConvertClient.exe"; WorkingDir: "{app}"
Name: "{group}\卸载{#MyShortcutName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyShortcutName}"; Filename: "{app}\SoundConvertClient.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{autodesktop}\{#MyShortcutName}.lnk"; Tasks: not desktopicon

[Run]
Filename: "{app}\SoundConvertClient.exe"; Description: "{cm:LaunchProgram,SoundConvertClient}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[CustomMessages]
chinesesimplified.Running=SoundConvertClient 前台或 SoundConvertEngine 后台仍在运行（包括托盘及其他登录会话）。请从托盘选择“退出”，确认前台和后台均已退出后重试。
english.Running=SoundConvertClient or SoundConvertEngine is running, possibly in the tray or another login session. Exit the application and its engine before retrying.
chinesesimplified.ProcessCheckFailed=无法检查程序运行状态，已停止操作。请确认 Windows Management Instrumentation (WMI) 服务可用后重试。
english.ProcessCheckFailed=Cannot check running processes. Verify that Windows Management Instrumentation (WMI) is available, then retry.
chinesesimplified.Downgrade=已安装较高版本或无法确认现有版本，不能覆盖。请使用相同或更高版本的安装包。
english.Downgrade=A newer or unrecognized version is installed. Use an installer of the same or a newer version.
chinesesimplified.MissingInstallation=未找到当前安装范围内的安装记录。修改和 /UPDATE 需要已有安装，请使用安装包进行首次安装。
english.MissingInstallation=No installation exists in this install scope. Maintenance and /UPDATE require an existing installation.
chinesesimplified.MaintenanceTitle=修改／升级现有安装
english.MaintenanceTitle=Modify or upgrade the existing installation
chinesesimplified.MaintenanceDescription=保留现有配置，修复程序文件并修改快捷方式选项。
english.MaintenanceDescription=Keep existing settings, repair program files and modify shortcut options.
chinesesimplified.Repair=修改／修复
english.Repair=Modify / repair
chinesesimplified.Upgrade=升级
english.Upgrade=Upgrade
chinesesimplified.MaintenanceDetails=已安装：V%1%n安装包：V%2%n目录：%3%n%n相同版本执行修改／修复，较新版本执行升级。下一页可调整桌面快捷方式。配置和设备记录保持原样。
english.MaintenanceDetails=Installed: V%1%nPackage: V%2%nFolder: %3%n%nThe same version repairs this installation; a newer version upgrades it. Desktop shortcut options can be changed on the following page. Settings and remembered devices are preserved.
chinesesimplified.DirectoryChanged=修改／升级必须使用原安装目录：%n%1%n如需迁移目录，请先卸载并保留配置，再重新安装。
english.DirectoryChanged=Maintenance / upgrade must use the existing folder:%n%1%nTo move the installation, uninstall while keeping settings, then reinstall.
chinesesimplified.UninstallTitle=卸载 SoundConvertClient
english.UninstallTitle=Uninstall SoundConvertClient
chinesesimplified.UninstallDescription=程序文件将被卸载。配置默认保留；选择下列选项才删除设置、配置备份和已记住的设备。日志与诊断文件保留。
english.UninstallDescription=Program files will be removed. Settings are kept unless selected below. Selected cleanup removes settings, backups and remembered devices. Logs and diagnostics are preserved.
chinesesimplified.DeleteCurrentConfig=删除当前用户的配置（不可恢复）
english.DeleteCurrentConfig=Delete the current user's settings (cannot be undone)
chinesesimplified.DeleteAllConfig=删除本机所有用户的本程序配置（不可恢复）
english.DeleteAllConfig=Delete this application's settings for all local users (cannot be undone)
chinesesimplified.ConfigCleanupFailed=部分配置无法删除，请检查卸载日志和用户配置文件权限。
english.ConfigCleanupFailed=Some settings could not be deleted. Check the uninstall log and profile permissions.

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#MyAppId}}_is1';
  ProfileListKey = 'Software\Microsoft\Windows NT\CurrentVersion\ProfileList';
var
  InstalledDirectory, InstalledVersion: String;
  ExistingInstallation, DeleteConfigSelected: Boolean;
  ConfigDirectories: TStringList;

function HasParameter(const Value: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then Result := True;
end;

function ApplicationStateError: String;
var Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    // WMI enumerates processes across sessions; no fragile window-title checks.
    Processes := Services.ExecQuery('SELECT Name FROM Win32_Process WHERE ' +
      'Name = ''SoundConvertClient.exe'' OR Name = ''SoundConvertEngine.exe''');
    if Processes.Count > 0 then Result := CustomMessage('Running');
  except
    Log('Process detection failed: ' + GetExceptionMessage);
    Result := CustomMessage('ProcessCheckFailed');
  end;
end;

function InstalledVersionError: String;
var OldVersion, PackageVersion: Int64;
begin
  Result := '';
  if ExistingInstallation then
  begin
    if not StrToVersion(InstalledVersion, OldVersion) then
      Result := CustomMessage('Downgrade')
    else if not StrToVersion('{#MyAppVersion}', PackageVersion) then
      Result := CustomMessage('Downgrade')
    else if ComparePackedVersion(OldVersion, PackageVersion) > 0 then
      Result := CustomMessage('Downgrade');
  end;
end;

function ShouldCacheSetup: Boolean;
begin
  Result := CompareText(ExpandFileName(ExpandConstant('{srcexe}')),
    ExpandFileName(ExpandConstant('{app}\Maintenance\SoundConvertClientSetup.exe'))) <> 0;
end;

function InstallScopeParameter(Param: String): String;
begin
  if IsAdminInstallMode then Result := '/ALLUSERS'
  else Result := '/CURRENTUSER';
end;

function InitializeSetup: Boolean;
var ErrorText: String;
begin
  ExistingInstallation := RegQueryStringValue(HKA, UninstallKey,
    'Inno Setup: App Path', InstalledDirectory);
  if ExistingInstallation then
    RegQueryStringValue(HKA, UninstallKey, 'DisplayVersion', InstalledVersion);
  ErrorText := ApplicationStateError;
  if ErrorText = '' then ErrorText := InstalledVersionError;
  if (ErrorText = '') and (HasParameter('/UPDATE') or HasParameter('/MODIFY')) and
     (not ExistingInstallation) then ErrorText := CustomMessage('MissingInstallation');
  Result := ErrorText = '';
  if not Result then SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK);
end;

procedure InitializeWizard;
begin
  if ExistingInstallation and (not WizardSilent) then
    CreateOutputMsgPage(wpWelcome, CustomMessage('MaintenanceTitle'),
      CustomMessage('MaintenanceDescription'), FmtMessage(CustomMessage('MaintenanceDetails'), [
      InstalledVersion, '{#MyAppVersion}', InstalledDirectory]));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if ExistingInstallation and (CurPageID = wpReady) then
  begin
    if InstalledVersion = '{#MyAppVersion}' then
      WizardForm.NextButton.Caption := CustomMessage('Repair')
    else WizardForm.NextButton.Caption := CustomMessage('Upgrade');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := ApplicationStateError;
  if Result = '' then Result := InstalledVersionError;
  if (Result = '') and ExistingInstallation and
     (CompareText(RemoveBackslashUnlessRoot(ExpandFileName(InstalledDirectory)),
      RemoveBackslashUnlessRoot(ExpandFileName(ExpandConstant('{app}')))) <> 0) then
    Result := FmtMessage(CustomMessage('DirectoryChanged'), [InstalledDirectory]);
end;

procedure RemoveOwnedLegacyShortcut(const ShortcutPath, TargetPath: String);
var Shell, Shortcut: Variant;
begin
  if not FileExists(ShortcutPath) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Shortcut := Shell.CreateShortcut(ShortcutPath);
    if CompareText(ExpandFileName(Shortcut.TargetPath), ExpandFileName(TargetPath)) = 0 then
    begin
      if not DeleteFile(ShortcutPath) then
        Log('Could not remove legacy shortcut: ' + ShortcutPath);
    end;
  except
    Log('Preserved unreadable legacy shortcut: ' + ShortcutPath + ': ' + GetExceptionMessage);
  end;
end;

procedure RemoveLegacyShortcuts;
var LegacyGroup: String;
begin
  RemoveOwnedLegacyShortcut(ExpandConstant('{autodesktop}\SoundConvertClient.lnk'),
    ExpandConstant('{app}\SoundConvertClient.exe'));
  LegacyGroup := ExpandConstant('{autoprograms}\SoundConvertClient');
  RemoveOwnedLegacyShortcut(LegacyGroup + '\SoundConvertClient.lnk',
    ExpandConstant('{app}\SoundConvertClient.exe'));
  RemoveOwnedLegacyShortcut(LegacyGroup + '\卸载 SoundConvertClient.lnk',
    ExpandConstant('{uninstallexe}'));
  RemoveOwnedLegacyShortcut(LegacyGroup + '\Uninstall SoundConvertClient.lnk',
    ExpandConstant('{uninstallexe}'));
  // Preserve any unrelated shortcuts or files in the previous group.
  RemoveDir(LegacyGroup);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ErrorText: String;
begin
  if CurStep = ssInstall then
  begin
    ErrorText := ApplicationStateError;
    if ErrorText <> '' then RaiseException(ErrorText);
  end;
  if CurStep = ssPostInstall then RemoveLegacyShortcuts;
end;

procedure AddConfigDirectory(const Directory: String);
begin
  if (Directory <> '') and (ConfigDirectories.IndexOf(Directory) < 0) then
    ConfigDirectories.Add(Directory);
end;

procedure CollectConfigDirectories;
var Profiles: TArrayOfString; I: Integer; ProfilePath: String; Shell: Variant;
begin
  ConfigDirectories := TStringList.Create;
  AddConfigDirectory(ExpandConstant('{localappdata}\UsbAudioBridge'));
  if not IsAdminInstallMode then Exit;
  // All-users cleanup is opt-in and explicitly labelled in the uninstall dialog.
  if RegGetSubkeyNames(HKLM64, ProfileListKey, Profiles) then
  begin
    Shell := CreateOleObject('WScript.Shell');
    for I := 0 to GetArrayLength(Profiles) - 1 do
      if (Pos('S-1-5-21-', Profiles[I]) = 1) or (Pos('S-1-12-1-', Profiles[I]) = 1) then
        if RegQueryStringValue(HKLM64, ProfileListKey + '\' + Profiles[I],
          'ProfileImagePath', ProfilePath) then
          AddConfigDirectory(AddBackslash(Shell.ExpandEnvironmentStrings(ProfilePath)) +
            'AppData\Local\UsbAudioBridge');
  end;
end;

function InitializeUninstall: Boolean;
var OptionsForm: TSetupForm; Description: TNewStaticText;
  DeleteCheck: TNewCheckBox; OkButton, CancelButton: TNewButton; ErrorText: String;
begin
  DeleteConfigSelected := False;
  ErrorText := ApplicationStateError;
  Result := ErrorText = '';
  if not Result then
  begin
    SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK);
    Exit;
  end;
  // Silent uninstall always preserves configuration, without an implicit purge.
  if UninstallSilent then Exit;
  OptionsForm := CreateCustomForm(ScaleX(540), ScaleY(190), True, True);
  try
    OptionsForm.Caption := CustomMessage('UninstallTitle');
    Description := TNewStaticText.Create(OptionsForm);
    Description.Parent := OptionsForm;
    Description.SetBounds(ScaleX(20), ScaleY(20), ScaleX(500), ScaleY(65));
    Description.AutoSize := False;
    Description.WordWrap := True;
    Description.Caption := CustomMessage('UninstallDescription');
    DeleteCheck := TNewCheckBox.Create(OptionsForm);
    DeleteCheck.Parent := OptionsForm;
    DeleteCheck.SetBounds(ScaleX(20), ScaleY(95), ScaleX(500), ScaleY(30));
    DeleteCheck.Checked := False;
    if IsAdminInstallMode then DeleteCheck.Caption := CustomMessage('DeleteAllConfig')
    else DeleteCheck.Caption := CustomMessage('DeleteCurrentConfig');
    OkButton := TNewButton.Create(OptionsForm);
    OkButton.Parent := OptionsForm;
    OkButton.SetBounds(ScaleX(320), ScaleY(145), ScaleX(95), ScaleY(28));
    OkButton.Caption := SetupMessage(msgButtonOK);
    OkButton.Default := True;
    OkButton.ModalResult := mrOk;
    CancelButton := TNewButton.Create(OptionsForm);
    CancelButton.Parent := OptionsForm;
    CancelButton.SetBounds(ScaleX(425), ScaleY(145), ScaleX(95), ScaleY(28));
    CancelButton.Caption := SetupMessage(msgButtonCancel);
    CancelButton.Cancel := True;
    CancelButton.ModalResult := mrCancel;
    Result := OptionsForm.ShowModal = mrOk;
    DeleteConfigSelected := Result and DeleteCheck.Checked;
    if DeleteConfigSelected then CollectConfigDirectories;
  finally
    OptionsForm.Free;
  end;
end;

procedure DeleteConfigFile(const Directory, Name: String; var Failed: Boolean);
var Path: String;
begin
  Path := AddBackslash(Directory) + Name;
  if FileExists(Path) and (not DeleteFile(Path)) then
  begin
    Failed := True;
    Log('Could not delete configuration: ' + Path);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var I: Integer; ErrorText, RunCommand: String; Failed: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    ErrorText := ApplicationStateError;
    if ErrorText <> '' then RaiseException(ErrorText);
    // Remove only the startup entry that points to this installed executable.
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'UsbAudioBridge', RunCommand) then
      if Pos('"' + Lowercase(ExpandConstant('{app}\SoundConvertClient.exe')) + '"',
        Lowercase(RunCommand)) = 1 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'UsbAudioBridge');
  end;
  if (CurUninstallStep = usPostUninstall) and DeleteConfigSelected then
  begin
    Failed := False;
    for I := 0 to ConfigDirectories.Count - 1 do
    begin
      // Exact configuration filenames only. Never recursively remove a profile.
      DeleteConfigFile(ConfigDirectories[I], 'settings.json', Failed);
      DeleteConfigFile(ConfigDirectories[I], 'settings.json.bak', Failed);
      DeleteConfigFile(ConfigDirectories[I], 'settings.json.tmp', Failed);
      DeleteConfigFile(ConfigDirectories[I], 'settings.schema1.bak', Failed);
      DeleteConfigFile(ConfigDirectories[I], 'devices.json', Failed);
      DeleteConfigFile(ConfigDirectories[I], 'devices.json.tmp', Failed);
      RemoveDir(ConfigDirectories[I]);
    end;
    if Failed then SuppressibleMsgBox(CustomMessage('ConfigCleanupFailed'), mbError, MB_OK, IDOK);
  end;
end;

procedure DeinitializeUninstall;
begin
  if Assigned(ConfigDirectories) then ConfigDirectories.Free;
end;
