; PassTheStick installer - Inno Setup
; Optional components: ViGEmBus, HidHide (install separately or bundle installers)
; Single PassTheStick.exe (unified app) plus bundled relay runtime.
; Note: relay runtime is bundled under {app}\relay for one-click local sessions.

#define MyAppName "PassTheStick"
; AppVersion is injected by CI via /DAppVersion=...
#ifndef AppVersion
  #define AppVersion "0.1.20"
#endif
#define MyAppPublisher "PassTheStick"
#define MyAppURL "https://github.com/passthestick/passthestick"
#define MyAppExeName "PassTheStick.exe"

[Setup]
; IMPORTANT: fixed AppId must never change between versions so Windows treats
; upgrades as the same app (silent upgrade).
AppId={{6A4F2D8C-2A9E-4E5B-9C5C-7B2D0C3B5A1F}}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
SetupIconFile=..\assets\passthestick.ico
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\output
OutputBaseFilename=PassTheStick-Setup-{#AppVersion}
Compression=lzma
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; publish\app\ contains PassTheStick.exe (unified host + guest) and shared runtime files
; and shared self-contained runtime files (so the runtime isn't duplicated per app).
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; Explicitly include the bundled relay runtime (produced into publish\app\relay\ by CI).
Source: "..\publish\app\relay\*"; DestDir: "{app}\relay"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Messages]
; Optional: add custom messages for ViGEm/HidHide download links
; Note: ViGEmBus and HidHide are optional; user can install manually. See README.
