; Audio Device Switcher 安装脚本 — Inno Setup 6
; 编译：双击 .iss 在 Inno Setup IDE 中按 F9，或命令行 ISCC.exe AudioDeviceSwitcher.iss
; 前置：先运行项目根目录的 publish.cmd 生成 publish\ 目录

#define AppName "Audio Device Switcher"
#define AppId_Name "AudioDeviceSwitcher"
#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#define AppPublisher "Chester"
#define AppExeName "AudioDeviceSwitcher.exe"
#define AppId "{{D7B3F2A1-9E45-4C8B-A0F1-3E5B8C7D2E91}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=AudioDeviceSwitcher-Setup-{#AppVersion}
SetupIconFile=..\CKit\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"; Flags: unchecked
Name: "autostart"; Description: "Start automatically on Windows logon"; GroupDescription: "Additional tasks:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; AppUserModelID: "{#AppId_Name}.App"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; AppUserModelID: "{#AppId_Name}.App"

[Registry]
; 可选开机自启（与应用内的"开机自启"开关共用同一注册表项）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppId_Name}"; ValueData: """{app}\{#AppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue
; Toast 通知需要的 AppUserModelID 注册（应用名 + 图标）
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppId_Name}.App"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppId_Name}.App"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\{#AppExeName}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前结束运行中的程序，避免文件占用
Filename: "taskkill"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir: string;
  Confirm: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppDataDir := ExpandConstant('{userappdata}\AudioDeviceSwitcher');
    if DirExists(AppDataDir) then
    begin
      Confirm := MsgBox('Also delete configuration files?' + #13#10 +
        'Location: ' + AppDataDir + #13#10 + #13#10 +
        'Choose "No" to keep your settings for future installs.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
      if Confirm = IDYES then
        DelTree(AppDataDir, True, True, True);
    end;
  end;
end;
