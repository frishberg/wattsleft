; Inno Setup script for the plain .exe download at wattsleft.app.
; Build: first `dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:Unpackaged=true -p:PublishDir=bin/unpackaged/win-x64/`
;        then `ISCC.exe Store\WattsLeft.iss`  -> Store\WattsLeft-Setup.exe

#define AppName "Watt's Left"
#define AppVersion "1.1.0"
#define Publisher "Aron Frishberg"
#define Url "https://wattsleft.app"
#define Src "..\bin\unpackaged\win-x64"

[Setup]
AppId={{7D2B4C8E-3F1A-4E9B-9C6D-2A5E8F0B1C37}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}
AppPublisher={#Publisher}
AppPublisherURL={#Url}
AppSupportURL={#Url}
DefaultDirName={autopf}\WattsLeft
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=.
OutputBaseFilename=WattsLeft-Setup
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\WattsLeft.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageFile=wizard-large.png
WizardSmallImageFile=wizard-small.png
WizardImageStretch=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; We terminate the tray app ourselves in [Code] before anything is copied,
; so Inno's Restart Manager prompt is neither needed nor wanted.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Src}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\WattsLeft.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\WattsLeft.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WattsLeft.exe"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WattsLeft"
; Remove the whole install folder, including anything written after install.
Type: filesandordirs; Name: "{app}"

[UninstallRun]
; Remove the "start with Windows" entry if it was switched on.
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v WattsLeft /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"

[Code]
{ Watt's Left runs in the tray, so closing its window doesn't quit it. We
  terminate the process at the very start of setup and uninstall, before any
  file is touched, so there is no Restart Manager prompt, no locked files left
  behind in Program Files, and no copy of the app still running afterwards. }
procedure KillApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/im WattsLeft.exe /f /t', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);
end;

function InitializeSetup(): Boolean;
begin
  KillApp;
  Result := True;
end;

function InitializeUninstall(): Boolean;
begin
  KillApp;
  Result := True;
end;
