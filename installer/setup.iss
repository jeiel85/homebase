; Homebase Setup — Inno Setup Script
; Build: iscc installer\setup.iss

#define AppName "Homebase"
; Version is injected by the release workflow via: iscc /DAppVersion=<tag>
; The guard keeps local `iscc installer\setup.iss` builds working with a dev default.
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#define AppPublisher "jeiel85"
#define AppURL "https://github.com/jeiel85/homebase"
#define ServiceName "Homebase.Agent"

[Setup]
AppId={{B8A3C4D5-E6F7-8A9B-0C1D-2E3F4A5B6C7D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={commonpf64}\Homebase
; Always offer the standard default folder, never a previously-remembered path — so a stale
; install location can't carry over. Users can still browse to a different folder.
UsePreviousAppDir=no
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\publish
OutputBaseFilename=Homebase-Setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=10.0.17763
ArchitecturesInstallIn64BitMode=x64compatible
DisableWelcomePage=no
CloseApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[Types]
Name: "full"; Description: "Full installation (Agent + Tray)"
Name: "agent"; Description: "Agent only (service, no tray)"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "agent"; Description: "Agent (Windows service)"; Types: full agent custom; Flags: fixed
Name: "tray"; Description: "Tray (system tray UI)"; Types: full custom

[Files]
; Agent binaries
Source: "..\publish\Agent\*"; DestDir: "{app}\Agent"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: agent
; Tray binaries
Source: "..\publish\Tray\*"; DestDir: "{app}\Tray"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: tray
; Config sample
Source: "..\config\appsettings.example.json"; DestDir: "{commonappdata}\Homebase\config"; Flags: ignoreversion onlyifdoesntexist
; PowerShell helpers (bundled alongside installer for reference)
Source: "setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "configure-telegram.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Homebase"; Filename: "{app}\Tray\Homebase.Tray.exe"; Components: tray
Name: "{group}\Uninstall Homebase"; Filename: "{uninstallexe}"

[Registry]
; Tray auto-start for current user
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Homebase.Tray"; ValueData: """{app}\Tray\Homebase.Tray.exe"""; Components: tray; Flags: uninsdeletevalue

[Run]
; setup.ps1 installs the service and Tray but leaves Telegram unconfigured
; (-SkipTelegram). The user enters the bot token and chat ID on first run, from
; the Tray welcome window, which applies them with a one-time elevation.
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\setup.ps1"" -SkipTelegram -NoInteractive -InstallDir ""{app}"" -AgentSource ""{app}\Agent"" -TraySource ""{app}\Tray"""; StatusMsg: "Installing service and tray..."; Flags: runhidden
; Launch the Tray so first-run onboarding appears right after install.
Filename: "{app}\Tray\Homebase.Tray.exe"; Description: "Start Homebase"; Flags: nowait postinstall skipifsilent; Components: tray

[UninstallRun]
; The Agent service is created with sc.exe, so the installer doesn't track it — remove it
; (plus auto-start and the bot-token env var) here, before files are deleted. Keeps config/data.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-service.ps1"" -InstallDir ""{app}"""; RunOnceId: "RemoveAgentService"; Flags: runhidden
