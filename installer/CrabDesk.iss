#define MyAppName "CrabDesk"
#ifndef MyAppVersion
  #define MyAppVersion "0.6.0"
#endif
#define MyAppPublisher "CrabDesk"
#define MyAppExeName "CrabDesk.App.exe"

[Setup]
AppId={{8AF9FCA9-D889-4ED7-B5A2-AC052B94016D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\CrabDesk
DefaultGroupName=CrabDesk
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=CrabDesk-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CrabDesk"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\CrabDesk"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他任务："; Flags: unchecked

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 CrabDesk"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--exit-existing"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopCrabDesk"

[Registry]
Root: HKCU; Subkey: "Software\Classes\DesktopBackground\Shell\CrabDesk"; Flags: uninsdeletekey dontcreatekey
