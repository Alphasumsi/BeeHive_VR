<#
.SYNOPSIS
    Baut alle BeeHive_VR-Komponenten und stellt sie im finalen Setup-Layout zusammen.

.DESCRIPTION
    Erzeugt das Staging-Verzeichnis, das der Inno-Setup-Installer 1:1 nach
    %LOCALAPPDATA%\Programs\BeeHive_VR ausrollt:

        <Staging>\
          BeeHiveVR.exe + .NET-self-contained + WebRoot\dashies-dist\   (dotnet publish)
          atlas\    (kompletter Electron-Ordner, npm run package)
          engine\   capture-host.exe, browser-host.exe, WebView2Loader.dll,
                    XR_APILAYER_NOVENDOR_beehive.dll + .json

    Reihenfolge: Engine (Layer + Hosts, x64 Release) -> Atlas (npm) -> WPF
    (self-contained publish) -> Assemblieren. Voraussetzung: iRacing geschlossen
    (sonst ist die Layer-DLL gelockt).

.PARAMETER Staging
    Zielordner (Default: .\release\staging, per .gitignore ausgeschlossen).

.PARAMETER SkipBuild
    Ueberspringt Engine- und Atlas-Build, assembliert nur aus vorhandenen Outputs
    (schnelle Iteration am Installer-Layout).

.PARAMETER SkipAtlas
    Baut die Engine, aber ueberspringt das langsame npm-Package des Atlas.
#>
[CmdletBinding()]
param(
    [string]$Staging = "$PSScriptRoot\release\staging",
    [switch]$SkipBuild,
    [switch]$SkipAtlas
)

$ErrorActionPreference = 'Stop'
$repo   = $PSScriptRoot
$config = 'Release'

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# --- Werkzeuge aufloesen -------------------------------------------------------
Step 'Werkzeuge aufloesen'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere nicht gefunden: $vswhere" }
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild nicht gefunden (VS mit C++/MSBuild installiert?)' }
Write-Host "MSBuild: $msbuild"

# --- iRacing-Lock-Check (Layer-DLL) -------------------------------------------
if (Get-Process 'iRacingSim64DX11' -ErrorAction SilentlyContinue) {
    throw 'iRacingSim64DX11 laeuft - bitte iRacing schliessen (Layer-DLL sonst gelockt).'
}

# --- Eigene Prozesse beenden (locken Build-Outputs) ---------------------------
Step 'Laufende BeeHive-Prozesse beenden'
'BeeHiveVR','BeeHive_VR_Atlas','capture-host','browser-host' | ForEach-Object {
    Get-Process $_ -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# --- Engine bauen (Layer + Hosts, x64 Release) --------------------------------
if (-not $SkipBuild) {
    Step 'Engine bauen (xr-api-beehive + capture-host + browser-host, x64 Release)'
    $sln = "$repo\engine\XR_APILAYER_NOVENDOR_beehive.sln"
    & $msbuild $sln -p:Configuration=$config -p:Platform=x64 -v:m -nologo '-t:xr-api-beehive;capture-host;browser-host'
    if ($LASTEXITCODE -ne 0) { throw 'Engine-Build fehlgeschlagen' }

    if (-not $SkipAtlas) {
        Step 'Atlas paketieren (npm run package)'
        Push-Location "$repo\app"
        try {
            npm run package
            if ($LASTEXITCODE -ne 0) { throw 'Atlas-Package fehlgeschlagen' }
        } finally { Pop-Location }
    }
}

# --- Staging vorbereiten ------------------------------------------------------
Step "Staging vorbereiten: $Staging"
if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }
New-Item -ItemType Directory -Force -Path $Staging | Out-Null

# --- WPF self-contained publish -> Staging-Root -------------------------------
Step 'WPF self-contained publish -> Staging-Root'
dotnet publish "$repo\wpf\BeeHive_VR\BeeHive_VR.csproj" -c $config -r win-x64 --self-contained true -o $Staging --nologo -v m
if ($LASTEXITCODE -ne 0) { throw 'WPF-Publish fehlgeschlagen' }

# --- Atlas -> Staging\atlas ---------------------------------------------------
Step 'Atlas -> Staging\atlas'
$atlasSrc = "$repo\app\out\BeeHive_VR_Atlas-win32-x64"
if (-not (Test-Path "$atlasSrc\BeeHive_VR_Atlas.exe")) {
    throw "Atlas-Output fehlt: $atlasSrc (npm run package noch nicht gelaufen? -SkipAtlas entfernen)"
}
New-Item -ItemType Directory -Force -Path "$Staging\atlas" | Out-Null
Copy-Item "$atlasSrc\*" -Destination "$Staging\atlas" -Recurse -Force

# --- Native + Layer -> Staging\engine -----------------------------------------
Step 'Native + Layer -> Staging\engine'
$eng = "$repo\engine\bin\x64\Release"
$engineFiles = @(
    'capture-host.exe',
    'browser-host.exe',
    'WebView2Loader.dll',
    'XR_APILAYER_NOVENDOR_beehive.dll',
    'XR_APILAYER_NOVENDOR_beehive.json'
)
New-Item -ItemType Directory -Force -Path "$Staging\engine" | Out-Null
foreach ($f in $engineFiles) {
    $src = Join-Path $eng $f
    if (-not (Test-Path $src)) { throw "Engine-Datei fehlt: $src (Engine-Build gelaufen?)" }
    Copy-Item $src -Destination "$Staging\engine" -Force
}

# --- Zusammenfassung ----------------------------------------------------------
Step 'Zusammenfassung'
$sizeMb = (Get-ChildItem -Recurse $Staging | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Gesamtgroesse: {0:N0} MB" -f $sizeMb)
Write-Host 'Top-Level:'
Get-ChildItem $Staging | ForEach-Object { Write-Host ("  " + $_.Name) }
Write-Host 'engine\:'
Get-ChildItem "$Staging\engine" | ForEach-Object { Write-Host ("  " + $_.Name) }
Write-Host "`nStaging fertig: $Staging" -ForegroundColor Green
