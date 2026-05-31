# Registers the BeeHive_VR OpenXR API layer as an implicit layer (loaded by every
# OpenXR app — entry.cpp filters down to iRacing). Run after Build.
# JSON is expected to sit next to this script (post-build copies both into OutDir).

$RegistryPath = "HKLM:\Software\Khronos\OpenXR\1\ApiLayers\Implicit"
$JsonPath = Join-Path "$PSScriptRoot" "XR_APILAYER_NOVENDOR_beehive.json"
Start-Process -FilePath powershell.exe -Verb RunAs -Wait -ArgumentList @"
	& {
		If (-not (Test-Path $RegistryPath)) {
			New-Item -Path $RegistryPath -Force | Out-Null
		}
		New-ItemProperty -Path $RegistryPath -Name '$JsonPath' -PropertyType DWord -Value 0 -Force | Out-Null
	}
"@
