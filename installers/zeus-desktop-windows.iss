; Zeus Desktop (Photino) Installer Script for Inno Setup
; Requires Inno Setup 6.2 or later: https://jrsoftware.org/isinfo.php
;
; Sibling to zeus-windows.iss (which packages the headless Zeus.Server
; service). This script packages Zeus.Desktop — the Photino in-process
; shell — so the operator gets a native window instead of having to open
; a browser. Distinct AppId from the service-mode installer so both can
; coexist on the same machine.

#define MyAppName "Zeus Desktop"
#define MyAppShortName "Zeus"
#define MyAppPublisher "Brian Keating (EI6LF) and contributors"
#define MyAppURL "https://github.com/brianbruff/openhpsdr-zeus"
#define MyAppExeName "Zeus.Desktop.exe"

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
; Distinct AppId from the service-mode installer (8F2E3B1C-... in
; zeus-windows.iss) so installing the desktop edition does NOT uninstall
; or upgrade an existing service-mode install. Operators can keep both
; for different workflows (browser-based remote vs native window local).
AppId={{B23E7F4A-1C8D-4DB6-9E5F-3A8C2B7D4E91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppShortName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=.\output
OutputBaseFilename=Zeus-Desktop-{#MyAppVersion}-win-{#Arch}-setup
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
Source: "..\Zeus.Desktop\bin\Release\net10.0\win-{#Arch}\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Shortcuts launch Zeus.Desktop.exe directly — Photino is the UI, no
; browser-open dance needed (the service-mode installer ships a .cmd
; launcher because it has to open a browser; we don't).
Name: "{group}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppShortName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppShortName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

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
