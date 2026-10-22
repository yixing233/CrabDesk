#define MyAppName "CrabDesk"
#ifndef MyAppVersion
  #define MyAppVersion "20260911.02"
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

[InstallDelete]
; Releases up to 1.0.0 shipped a self-contained flavor that put the .NET host
; app-local. Inno Setup only overwrites files present in the new payload, so
; upgrading from that flavor leaves hostfxr.dll behind, which pins the .NET
; root to {app}; no shared\Microsoft.NETCore.App exists there and startup dies
; with 0x80008096 ("You must install or update .NET") even though the machine
; runtime is fine. Delete the whole app-local runtime layer before copying.
; Keep this list in sync with the guard in build\publish.ps1.
Type: files; Name: "{app}\clrcompression.dll"
Type: files; Name: "{app}\clretwrc.dll"
Type: files; Name: "{app}\clrgc.dll"
Type: files; Name: "{app}\clrjit.dll"
Type: files; Name: "{app}\coreclr.dll"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\hostfxr.dll"
Type: files; Name: "{app}\hostpolicy.dll"
Type: files; Name: "{app}\Microsoft.WindowsAppRuntime.dll"
Type: files; Name: "{app}\mscordaccore*.dll"
Type: files; Name: "{app}\mscordbi.dll"
Type: files; Name: "{app}\mscorrc.dll"
Type: files; Name: "{app}\mscorlib.dll"
Type: files; Name: "{app}\netstandard.dll"
Type: files; Name: "{app}\System.Private.CoreLib.dll"

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
Root: HKCU; Subkey: "Software\Classes\CrabDesk.DesktopContextMenu.Commands"; Flags: uninsdeletekey dontcreatekey
