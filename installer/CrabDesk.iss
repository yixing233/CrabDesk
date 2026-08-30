#define MyAppName "CrabDesk"
#ifndef MyAppVersion
  #define MyAppVersion "20260830.01"
#endif
#define MyAppPublisher "CrabDesk"
#define MyAppExeName "CrabDesk.WinUI.exe"
#ifndef MyPublishPath
  #define MyPublishPath "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "CrabDesk-Payload-x64"
#endif

[Setup]
AppId={{8AF9FCA9-D889-4ED7-B5A2-AC052B94016D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\CrabDesk
DefaultGroupName=CrabDesk
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\CrabDesk.WinUI\Assets\CrabDesk.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "{#MyPublishPath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
