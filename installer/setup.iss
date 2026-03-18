; PassTheStick installer - Inno Setup
; Optional components: ViGEmBus, HidHide (install separately or bundle installers)
; Install Host + Guest + Launcher to one folder.

#define MyAppName "PassTheStick"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "PassTheStick"
#define MyAppURL "https://github.com/passthestick/passthestick"
#define MyAppExeName "PassTheStick.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-PASSTHESTICK}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\output
OutputBaseFilename=PassTheStick-Setup-{#MyAppVersion}
Compression=lzma
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Use a single publish\app\ folder containing PassTheStick.exe, PassTheStick.Host.exe, PassTheStick.Guest.exe
; and shared self-contained runtime files (so the runtime isn't duplicated per app).
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Host only"; Filename: "{app}\PassTheStick.Host.exe"
Name: "{group}\Guest only"; Filename: "{app}\PassTheStick.Guest.exe"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Messages]
; Optional: add custom messages for ViGEm/HidHide download links
; Note: ViGEmBus and HidHide are optional; user can install manually. See README.
