; ============================================================================
;  BeeHive_VR - Inno Setup Installer
;  Erzeugt eine per-user setup.exe (KEIN Admin) aus dem Staging-Verzeichnis,
;  das build-release.ps1 unter ..\release\staging erzeugt.
;
;  Kompilieren: Inno Setup 6.3+ installieren, dann dieses Script mit ISCC.exe
;  (oder der IDE) bauen. Vorher build-release.ps1 laufen lassen.
;
;  Entscheidungen (s. project_installer_plan): per-user %LOCALAPPDATA%\Programs,
;  HKCU-Layer-Registrierung, kein Signing, WebView2-Bootstrapper als Fallback,
;  Uninstaller mit Config-Abfrage, alles UK-Englisch.
; ============================================================================

#define MyAppName        "BeeHive_VR"
#define MyAppVersion      "0.10.8"
#define MyAppPublisher    "BeeHive_VR"
#define MyAppURL          "https://github.com/Alphasumsi/BeeHive_VR"
#define MyAppExeName      "BeeHiveVR.exe"
#define StagingDir        "..\release\staging"
#define LayerJsonRel      "engine\XR_APILAYER_NOVENDOR_beehive.json"

; WebView2 Evergreen Bootstrapper (optional). Liegt er in installer\redist\,
; wird er gebuendelt und bei fehlender Runtime still ausgefuehrt. Fehlt er,
; kompiliert das Script trotzdem (nur ohne WebView2-Fallback).
#define WV2Rel            "redist\MicrosoftEdgeWebview2Setup.exe"
#define HaveWV2           FileExists(AddBackslash(SourcePath) + WV2Rel)

[Setup]
; Stabile AppId - NICHT aendern (sonst kein In-Place-Update).
AppId={{B7E5B0A1-9C2D-4E3F-A6B8-1F2C3D4E5F60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}.0

; Per-user, kein Admin.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
; Pfad-Auswahl ausblenden -> installiert automatisch nach DefaultDirName.
; Desktop-Shortcut-Abfrage (Tasks-Seite) bleibt erhalten.
DisableDirPage=yes
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; Nur 64-bit (Inno 6.3+). Kein harter Win11-Gate - WebView2 deckt der Bootstrapper.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Waehrend Install/Update unsere eigenen Prozesse via Restart-Manager schliessen
; (locken sonst die Dateien). iRacing wird separat im [Code] geprueft (Abbruch).
CloseApplications=yes
RestartApplications=no

SetupIconFile=..\wpf\BeeHive_VR\Assets\bee_icon_256.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\release
OutputBaseFilename=BeeHive_VR-Setup-{#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; UK-Englisch. Windows-eigene Dialoge folgen weiter der OS-Sprache.
english.IRacingRunning=iRacing is running. Please close iRacing before continuing, then run this installer again.
english.RemoveSettingsPrompt=Do you also want to remove your personal BeeHive_VR settings (layouts, configuration and logs)?%n%nChoose No to keep them for a future re-install.
english.InstallingWebView2=Installing the WebView2 runtime...

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Gesamtes Staging-Layout nach {app} (BeeHiveVR.exe + .NET self-contained +
; WebRoot\ + atlas\ + engine\).
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
#if HaveWV2
Source: "{#WV2Rel}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2
#endif

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; OpenXR-Implicit-Layer registrieren (HKCU = kein Admin). ValueName = absoluter
; Pfad zur JSON, DWORD 0 = aktiv. uninsdeletevalue raeumt ihn beim Deinstallieren
; wieder weg (sonst verwaister Layer -> stoert JEDE OpenXR-App). Den Implicit-
; Schluessel selbst NICHT loeschen (gehoert OpenXR).
Root: HKCU; Subkey: "Software\Khronos\OpenXR\1\ApiLayers\Implicit"; \
    ValueType: dword; ValueName: "{app}\{#LayerJsonRel}"; ValueData: 0; \
    Flags: uninsdeletevalue

[Run]
#if HaveWV2
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
    StatusMsg: "{cm:InstallingWebView2}"; Check: NeedsWebView2; Flags: waituntilterminated
#endif
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[Code]
{ --- WMI-Prozesscheck: laeuft ExeName gerade? --------------------------------- }
function IsProcessRunning(const ExeName: string): Boolean;
var
  Locator, Service, Items: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\CIMV2');
    Items := Service.ExecQuery(
      'SELECT Name FROM Win32_Process WHERE Name=''' + ExeName + '''');
    Result := Items.Count > 0;
  except
    Result := False; { WMI nicht verfuegbar -> nicht blockieren }
  end;
end;

{ --- WebView2-Evergreen-Runtime vorhanden? ------------------------------------ }
function WebView2Installed(): Boolean;
var
  pv: string;
begin
  Result := False;
  if RegQueryStringValue(HKLM,
      'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
      'pv', pv) then
    if (pv <> '') and (pv <> '0.0.0.0') then
      Result := True;
  if not Result then
    if RegQueryStringValue(HKCU,
        'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'pv', pv) then
      if (pv <> '') and (pv <> '0.0.0.0') then
        Result := True;
end;

function NeedsWebView2(): Boolean;
begin
  Result := not WebView2Installed();
end;

{ --- iRacing-Lock-Check: nur iRacingSim64DX11.exe laedt/lockt die Layer-DLL ---- }
function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsProcessRunning('iRacingSim64DX11.exe') then
  begin
    MsgBox(ExpandConstant('{cm:IRacingRunning}'), mbError, MB_OK);
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsProcessRunning('iRacingSim64DX11.exe') then
  begin
    MsgBox(ExpandConstant('{cm:IRacingRunning}'), mbError, MB_OK);
    Result := False;
  end;
end;

{ --- Beim Deinstallieren: optionale Abfrage, ob Nutzer-Config mit weg soll ----- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox(ExpandConstant('{cm:RemoveSettingsPrompt}'), mbConfirmation, MB_YESNO) = IDYES then
    begin
      DataDir := ExpandConstant('{localappdata}\BeeHive_VR');
      if DirExists(DataDir) then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
