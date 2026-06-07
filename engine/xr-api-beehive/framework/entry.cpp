// MIT License
//
// Copyright(c) 2021-2023 Matthieu Bucchianeri
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this softwareand associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and /or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright noticeand this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

#include "pch.h"

#include <layer.h>

#include "dispatch.h"
#include "log.h"

namespace openxr_api_layer {
    // The path where the DLL is loaded from (eg: to load data files).
    std::filesystem::path dllHome;

    // The path that is writable (eg: to store logs).
    std::filesystem::path localAppData;

    namespace log {
        // The file logger.
        std::ofstream logStream;
    } // namespace log
} // namespace openxr_api_layer

using namespace openxr_api_layer;
using namespace openxr_api_layer::log;

extern "C" {

// Entry point for the loader.
XrResult __declspec(dllexport) XRAPI_CALL
    xrNegotiateLoaderApiLayerInterface(const XrNegotiateLoaderInfo* const loaderInfo,
                                       const char* const apiLayerName,
                                       XrNegotiateApiLayerRequest* const apiLayerRequest) {
    // App-Targeting: nur für bekannte Ziel-Apps Layer aktivieren. Für alles andere
    // Negotiation ablehnen → OpenXR-Loader entfernt uns komplett aus der Chain.
    // Echter No-Op gegenüber anderen VR-Apps (z.B. Virtual-Desktop-Streamer-Spielen),
    // bei denen reine Anwesenheit des Layers Achsen-Inversion in VDXR triggert.
    {
        char exePath[_MAX_PATH] = {};
        GetModuleFileNameA(nullptr, exePath, sizeof(exePath));
        std::string exeName = std::filesystem::path(exePath).filename().string();
        std::transform(exeName.begin(), exeName.end(), exeName.begin(),
                       [](unsigned char c) { return (char)std::tolower(c); });
        if (exeName.find("iracingsim64dx11") == std::string::npos) {
            return XR_ERROR_INITIALIZATION_FAILED;
        }
    }

    TraceLoggingWrite(g_traceProvider, "xrNegotiateLoaderApiLayerInterface");

    // Retrieve the path of the DLL.
    if (dllHome.empty()) {
        HMODULE module;
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               (LPCSTR)&dllHome,
                               &module)) {
            char path[_MAX_PATH];
            GetModuleFileNameA(module, path, sizeof(path));
            dllHome = std::filesystem::path(path).parent_path();
        }
    }

    // Project-wide LocalAppData folder. ALL BeeHive_VR components (engine, app,
    // companion) write into the same %LOCALAPPDATA%\BeeHive_VR\ root so logs are
    // discoverable from one place. The OpenXR layer is the "engine" component.
    localAppData = std::filesystem::path(getenv("LOCALAPPDATA")) / "BeeHive_VR";
    CreateDirectoryA(localAppData.string().c_str(), nullptr);
    const auto logsDir = localAppData / "logs";
    CreateDirectoryA(logsDir.string().c_str(), nullptr);

    // Start logging to file. Filename = "engine.log" so it sits alongside
    // app.log and atlas.log in the logs/ subfolder. F3 (5.6.2026): bei >3 MB
    // einmal nach .old rotieren bevor der Stream geöffnet wird — analog
    // zur Atlas-/WPF-Logger-Politik. Runtime-Rotation in log.cpp deckt
    // ultra-lange Sessions ab.
    if (!logStream.is_open()) {
        std::filesystem::path logFile = logsDir / "engine.log";
        try {
            if (std::filesystem::exists(logFile) &&
                std::filesystem::file_size(logFile) > (3 * 1024 * 1024)) {
                std::filesystem::path oldFile = logFile;
                oldFile += ".old";
                std::error_code ec;
                std::filesystem::remove(oldFile, ec);     // .old kann fehlen, OK
                std::filesystem::rename(logFile, oldFile, ec); // best-effort
            }
        } catch (...) {
            // Logger-Setup darf nie werfen — silent fall-through zu open()
        }
        logStream.open(logFile.string(), std::ios_base::ate);
    }

    DebugLog("--> xrNegotiateLoaderApiLayerInterface\n");

    if (apiLayerName && std::string_view(apiLayerName) != LAYER_NAME) {
        ErrorLog(fmt::format("Invalid apiLayerName \"{}\"\n", apiLayerName));
        return XR_ERROR_INITIALIZATION_FAILED;
    }

    if (!loaderInfo || !apiLayerRequest || loaderInfo->structType != XR_LOADER_INTERFACE_STRUCT_LOADER_INFO ||
        loaderInfo->structVersion != XR_LOADER_INFO_STRUCT_VERSION ||
        loaderInfo->structSize != sizeof(XrNegotiateLoaderInfo) ||
        apiLayerRequest->structType != XR_LOADER_INTERFACE_STRUCT_API_LAYER_REQUEST ||
        apiLayerRequest->structVersion != XR_API_LAYER_INFO_STRUCT_VERSION ||
        apiLayerRequest->structSize != sizeof(XrNegotiateApiLayerRequest) ||
        loaderInfo->minInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->maxInterfaceVersion < XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->maxInterfaceVersion > XR_CURRENT_LOADER_API_LAYER_VERSION ||
        loaderInfo->maxApiVersion < XR_CURRENT_API_VERSION || loaderInfo->minApiVersion > XR_CURRENT_API_VERSION) {
        ErrorLog("xrNegotiateLoaderApiLayerInterface validation failed\n");
        return XR_ERROR_INITIALIZATION_FAILED;
    }

    // Setup our layer to intercept OpenXR calls.
    apiLayerRequest->layerInterfaceVersion = XR_CURRENT_LOADER_API_LAYER_VERSION;
    apiLayerRequest->layerApiVersion = XR_CURRENT_API_VERSION;
    apiLayerRequest->getInstanceProcAddr = reinterpret_cast<PFN_xrGetInstanceProcAddr>(xrGetInstanceProcAddr);
    apiLayerRequest->createApiLayerInstance = reinterpret_cast<PFN_xrCreateApiLayerInstance>(xrCreateApiLayerInstance);

    DebugLog("<-- xrNegotiateLoaderApiLayerInterface\n");

    Log(fmt::format("{} layer ({}) is active\n", LayerName, VersionString));

    TraceLoggingWrite(g_traceProvider, "xrNegotiateLoaderApiLayerInterface_Complete");

    return XR_SUCCESS;
}
}
