# BeeHive_VR — Installer-Build

Erzeugt eine per-user `setup.exe` (kein Admin) aus dem Staging-Verzeichnis.

## Voraussetzungen (einmalig)

1. **Inno Setup 6.3+** installieren (kostenlos):
   ```
   winget install --id JRSoftware.InnoSetup
   ```
   Danach liegt `ISCC.exe` unter `%ProgramFiles(x86)%\Inno Setup 6\`.

2. **WebView2-Bootstrapper** (optional, ~2 MB) als Fallback bündeln:
   `MicrosoftEdgeWebview2Setup.exe` von Microsoft laden und nach
   `installer\redist\MicrosoftEdgeWebview2Setup.exe` legen.
   Fehlt die Datei, kompiliert das Script trotzdem — dann ohne WebView2-Fallback
   (auf Win11 egal, Runtime ist da).

## Release bauen

```powershell
# 1. Alle Komponenten bauen + ins Staging-Layout sammeln
.\build-release.ps1                 # frischer Rebuild (iRacing vorher schliessen!)
# .\build-release.ps1 -SkipBuild    # nur assemblieren aus vorhandenen Outputs

# 2. Installer kompilieren
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\BeeHive_VR.iss
```

Ergebnis: `release\BeeHive_VR-Setup-<version>.exe`.

## Was der Installer tut

- Install nach `%LOCALAPPDATA%\Programs\BeeHive_VR` (per-user, kein Admin),
  Unterordner `atlas\` + `engine\`.
- Registriert den OpenXR-Layer in **HKCU** (`…\Khronos\OpenXR\1\ApiLayers\Implicit`).
- WebView2-Bootstrapper nur, falls Runtime fehlt.
- Start-Menü- + optionale Desktop-Verknüpfung.
- **iRacing-Check:** läuft `iRacingSim64DX11.exe`, bricht Install/Uninstall mit
  Meldung ab (Layer-DLL sonst gelockt). Eigene Prozesse schließt Inno automatisch.

## Uninstaller

- Über „Apps & Features" (per-user, kein Admin).
- Entfernt Dateien + den HKCU-Layer-Wert.
- Fragt, ob die persönliche Config (`%LOCALAPPDATA%\BeeHive_VR`) auch weg soll.

## Nutzer-Daten bleiben getrennt

`%LOCALAPPDATA%\Programs\BeeHive_VR` = Programm (Uninstall löscht das).
`%LOCALAPPDATA%\BeeHive_VR` = Layouts/Settings/Logs (bleibt, außer der Nutzer
bestätigt die Lösch-Abfrage).
