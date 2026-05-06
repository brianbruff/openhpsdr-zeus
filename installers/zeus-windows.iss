; Zeus Installer Script for Inno Setup
; Requires Inno Setup 6.2 or later: https://jrsoftware.org/isinfo.php

#define MyAppName "Zeus"
#define MyAppPublisher "Brian Keating (EI6LF) and contributors"
#define MyAppURL "https://github.com/brianbruff/openhpsdr-zeus"
#define MyAppExeName "Zeus.Server.exe"

; Version will be passed via /DMyAppVersion="x.y.z" command line parameter
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

; Target architecture: pass /DArch=arm64 from CI to build the Windows-on-ARM
; installer. Defaults to x64 so local iscc runs without explicit args still
; produce the historical x64 installer. Inno Setup 6.3+ accepts both "x64"
; and "arm64" as ArchitecturesAllowed identifiers.
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{8F2E3B1C-9A4D-4E6F-B7C3-1D5A9E8F2B4C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=.\output
OutputBaseFilename=Zeus-{#MyAppVersion}-win-{#Arch}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64 arm64
ArchitecturesAllowed={#Arch}
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\Zeus.Server\bin\Release\net10.0\win-{#Arch}\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "zeus-windows-launcher.cmd"; DestDir: "{app}"; DestName: "zeus.cmd"; Flags: ignoreversion

[Icons]
; Shortcuts launch zeus.cmd (which boots Zeus.Server and opens the browser at
; http://localhost:6060) instead of Zeus.Server.exe directly. IconFilename
; keeps the Zeus.Server.exe icon on the shortcut so it doesn't show as a cmd.
Name: "{group}\{#MyAppName}"; Filename: "{app}\zeus.cmd"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\zeus.cmd"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\zeus.cmd"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsWin64 then
  begin
    MsgBox('This application requires Windows 64-bit.', mbError, MB_OK);
    Result := False;
  end;
end;
