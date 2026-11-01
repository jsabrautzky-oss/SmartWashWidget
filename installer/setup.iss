;=============================================================================
; SmartWashWidget - Inno Setup Installation (Windows 11 x64)
; Die Version steht unten bei MyAppVersion (gleiche Zahl wie in der .csproj).
;
; Voraussetzung: zuvor build_release.bat ausfuehren, damit
; ..\publish\SmartWashWidget.exe existiert (Single-File, self-contained).
;
; Uebersetzen mit:  ISCC.exe installer\setup.iss
;=============================================================================

#define MyAppName "SmartWashWidget"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "jsabrautzky-oss"
#define MyAppExeName "SmartWashWidget.exe"
#define MyAppWebsite "https://github.com/jsabrautzky-oss/SmartWashWidget"

[Setup]
; AppId NICHT aendern - sie Identifiziert die Installation bei Updates/Deinstallation.
AppId={{9E4F2C8A-6B41-4B0E-9C44-4A8E7E9A11D7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright="Copyright (c) 2026 jsabrautzky-oss - MIT"
AppSupportURL={#MyAppWebsite}
AppUpdatesURL={#MyAppWebsite}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; Dasselbe Symbol wie die EXE (rundes Bullauge) - siehe tools\icon-gen.
SetupIconFile=..\Assets\SmartWashWidget.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "Zusaetzliche Verknuepfungen:"
Name: "autostart"; Description: "Beim Windows-Start automatisch starten (Empfehlung)"; GroupDescription: "Verhalten:"; Flags: unchecked

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
; Autostart-Ueber die Autostart-Mappe: bei Installation als Administrator die
; "Alle Benutzer"-Mappe, bei Installation ohne Rechte die Mappe des aktuellen Benutzers.
Name: "{commonstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\{#MyAppExeName}"
Type: filesandordirs; Name: "{app}"

[Code]
// Die personliche Konfiguration (inkl. DPAPI-verschluesseltem Token) liegt in
// %LOCALAPPDATA%\SmartWashWidget und wird bei der Deinstallation absichtlich
// NICHT geloescht - sie enthaelt keine geheimen Daten ausserhalb des eigenen
// Windows-Benutzerkontos. Ein Hinweis genuegt daher.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if DirExists(ExpandConstant('{localappdata}\SmartWashWidget')) then
      MsgBox('Die persoenliche Konfiguration in %LOCALAPPDATA%\SmartWashWidget ' +
             '(einschliesslich des verschluesselten SmartThings-Tokens) wurde ' +
             'behalten. Entfernen Sie den Ordner manuell, wenn Sie auch das ' +
             'Token loeschen moechten.', mbInformation, MB_OK);
  end;
end;
