// capture-host — minimales pch. Stellt genau das bereit, was ..\xr-api-beehive\utils\capture.h
// (UNVERÄNDERT inkludiert, single source of truth für C/C2-Throttle) und main.cpp brauchen.
// Kein framework/-Dependency des Layers — capture-host ist bewusst standalone wie browser-host.

#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <unknwn.h>      // klassisches COM VOR winrt (Interop-Muster)
#include <inspectable.h>

#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <wrl.h>
using Microsoft::WRL::ComPtr;

#include <winrt/base.h>
#include <winrt/windows.foundation.h>
#include <winrt/windows.graphics.capture.h>
#include <windows.graphics.capture.interop.h>
#include <winrt/windows.graphics.directx.direct3d11.h>

#include <chrono>
#include <cstdint>
#include <string>

// capture.h erwartet das Layer-Makro; hier als winrt-Äquivalent (wirft hresult_error,
// Call-Sites fangen wie im Layer per try/catch).
#ifndef CHECK_HRCMD
#define CHECK_HRCMD(cmd) winrt::check_hresult(cmd)
#endif
