; UberDisk installer (Inno Setup 6). Built by build.cmd release, through UberDisk.Setup.proj.
;
; Installs for the current user only, with no admin rights: the one-file UberDisk.exe goes to
; %LOCALAPPDATA%\Programs\UberDisk, with a Start menu entry and an entry under Settings > Apps for uninstalling.
; The app updates itself the same way installed or portable (it swaps in the release's UberDisk.exe), and the
; folder is the user's own, so updates need no admin rights either.

#ifndef SourceExe
  #define SourceExe "..\dist\UberDisk\UberDisk.exe"
#endif
#define AppVersion GetStringFileInfo(SourceExe, "ProductVersion")
#if AppVersion == ""
  #error Can't read the version of the exe; run build.cmd release first.
#endif
#define AppUrl "https://github.com/OleHalvor/UberDisk"

[Setup]
; Never change AppId: it ties upgrades and the uninstall entry together. MainWindow.Update.cs uses it too.
AppId={{C58BCDC3-D240-45E9-9853-868DAF129E2A}
AppName=UberDisk
AppVersion={#AppVersion}
AppVerName=UberDisk {#AppVersion}
AppPublisher=OleHalvor
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName=UberDisk
VersionInfoDescription=UberDisk installer
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\UberDisk
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\UberDisk.App\Assets\UberDisk.ico
UninstallDisplayIcon={app}\UberDisk.exe
UninstallDisplayName=UberDisk
; A running UberDisk is closed (Restart Manager) before its exe is replaced or removed.
CloseApplications=yes
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=UberDisk-Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; No ignoreversion: if UberDisk already updated itself past this installer's version, the newer exe stays.
Source: "{#SourceExe}"; DestDir: "{app}"

[Icons]
Name: "{autoprograms}\UberDisk"; Filename: "{app}\UberDisk.exe"
Name: "{autodesktop}\UberDisk"; Filename: "{app}\UberDisk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\UberDisk.exe"; Description: "{cm:LaunchProgram,UberDisk}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Left behind by the self-updater. Settings in %APPDATA%\UberDisk are kept.
Type: files; Name: "{app}\UberDisk.exe.old"
Type: files; Name: "{app}\UberDisk.exe.download"
Type: dirifempty; Name: "{app}"
