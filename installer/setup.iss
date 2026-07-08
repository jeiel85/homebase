; LocalOps Bot Setup — Inno Setup Script
; Build: iscc installer\setup.iss

#define AppName "LocalOps Bot"
#define AppVersion "0.1.0"
#define AppPublisher "jeiel85"
#define AppURL "https://github.com/jeiel85/localops-bot"
#define ServiceName "LocalOpsBot.Agent"

[Setup]
AppId={{B8A3C4D5-E6F7-8A9B-0C1D-2E3F4A5B6C7D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={commonpf64}\LocalOpsBot
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\publish
OutputBaseFilename=LocalOpsBot.Setup
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
Source: "..\config\appsettings.example.json"; DestDir: "{commonappdata}\LocalOpsBot\config"; Flags: ignoreversion onlyifdoesntexist
; PowerShell helpers (bundled alongside installer for reference)
Source: "setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-service.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\LocalOps Bot"; Filename: "{app}\Tray\LocalOpsBot.Tray.exe"; Components: tray
Name: "{group}\Uninstall LocalOps Bot"; Filename: "{uninstallexe}"

[Registry]
; Tray auto-start for current user
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LocalOpsBot.Tray"; ValueData: """{app}\Tray\LocalOpsBot.Tray.exe"""; Components: tray; Flags: uninsdeletevalue

[Run]
; The actual setup.ps1 handles service creation, env vars, and token/chat ID
; We call it post-install with the values collected in the wizard.
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -File ""{app}\setup.ps1"" -Token ""{code:GetToken}"" -ChatId ""{code:GetChatId}"" -NoInteractive -AgentSource ""{app}\Agent"" -TraySource ""{app}\Tray"""; StatusMsg: "Configuring service and environment..."; Flags: runhidden

[Code]
var
  TokenPage: TInputQueryWizardPage;
  ChatIdPage: TInputQueryWizardPage;

function GetToken(Param: string): string;
begin
  Result := TokenPage.Values[0];
end;

function GetChatId(Param: string): string;
begin
  Result := ChatIdPage.Values[0];
end;

procedure InitializeWizard;
begin
  TokenPage := CreateInputQueryPage(wpSelectComponents,
    'Telegram Bot Token', 'Enter your bot token from @BotFather',
    'Create a bot at https://t.me/BotFather using /newbot, then paste the token below.' + #13#10 +
    'Format: 1234567890:ABC-DEF1234ghIkl-zyx57W2v1u123ew11');
  TokenPage.Add('Bot token:', False);
  TokenPage.Values[0] := GetEnv('LOCALOPSBOT_TELEGRAM_TOKEN');

  ChatIdPage := CreateInputQueryPage(TokenPage.ID,
    'Telegram Chat ID', 'Enter the chat ID that will control this bot',
    'Send any message to your bot, then visit:' + #13#10 +
    'https://api.telegram.org/bot{your_token}/getUpdates' + #13#10 +
    'Look for the "chat"."id" value (e.g. 123456789).');
  ChatIdPage.Add('Chat ID:', False);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Token, ChatId: string;
begin
  Result := True;
  if CurPageID = TokenPage.ID then
  begin
    Token := Trim(TokenPage.Values[0]);
    if Token = '' then
    begin
      MsgBox('Bot token is required. You can get one from @BotFather on Telegram.', mbError, MB_OK);
      Result := False;
    end
    else if not (Token[1] in ['0'..'9']) then
    begin
      MsgBox('Token should start with digits (e.g. 123456:ABC...).', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = ChatIdPage.ID then
  begin
    ChatId := Trim(ChatIdPage.Values[0]);
    if ChatId = '' then
    begin
      MsgBox('Chat ID is required.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Ensure the machine env var is set (setup.ps1 will also do this, but double-check)
    if TokenPage.Values[0] <> '' then
      SaveStringToFile(ExpandConstant('{commonappdata}\LocalOpsBot\.token'), TokenPage.Values[0], False);
  end;
end;
