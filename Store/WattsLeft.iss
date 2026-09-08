; Inno Setup script for the plain .exe download at wattsleft.app.
; Build: first `dotnet publish -c Release -r win-x64 -p:Platform=x64 -p:Unpackaged=true -p:PublishDir=bin/unpackaged/win-x64/`
;        then `ISCC.exe Store\WattsLeft.iss`  -> Store\WattsLeft-Setup.exe

#define AppName "Watt's Left"
#define AppVersion "1.0.0"
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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
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

[UninstallRun]
; Remove the "start with Windows" entry if it was switched on.
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v WattsLeft /f"; Flags: runhidden; RunOnceId: "RemoveRunKey"
