# Removes the BeeHive_VR OpenXR API layer registration. Run before uninstalling
# the build, or to disable the layer without deleting the DLL.

$JsonPath = Join-Path "$PSScriptRoot" "XR_APILAYER_NOVENDOR_beehive.json"
Start-Process -FilePath powershell.exe -Verb RunAs -Wait -ArgumentList @"
	& {
		Remove-ItemProperty -Path HKLM:\Software\Khronos\OpenXR\1\ApiLayers\Implicit -Name '$JsonPath' -Force | Out-Null
	}
"@
