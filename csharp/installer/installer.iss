; Inno Setup Script for RoomRelay
; Requires Inno Setup 6.2+: https://jrsoftware.org/isdl.php
; Compile with: iscc installer.iss

#define MyAppName "RoomRelay"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "guicn555"
#define MyAppURL "https://github.com/guicn555/RoomRelay"
#define MyAppExeName "RoomRelay.exe"

[Setup]
AppId={{E2A3F4C5-6B7D-4E8F-9A0B-1C2D3E4F5A6B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=RoomRelay-Setup-{#MyAppVersion}
OutputDir=..
SetupIconFile=..\src\SonosStreaming.App\Assets\sonos-streaming.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\RoomRelay-v1.0.5\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
