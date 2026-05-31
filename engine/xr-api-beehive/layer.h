// BeeHive_VR — OpenXR API layer (XR_APILAYER_NOVENDOR_beehive)
//
// Based on https://github.com/mbucchia/OpenXR-Layer-Template (MIT).
// Copyright(c) 2022-2023 Matthieu Bucchianeri.

#pragma once

#include "framework/dispatch.gen.h"

namespace openxr_api_layer {

    const std::string LayerName = LAYER_NAME;
    const std::string VersionString = "Step3 (0.0.6) — Multi-Region Atlas";

    OpenXrApi* GetInstance();

    extern std::filesystem::path dllHome;
    extern std::filesystem::path localAppData;

    extern const std::vector<std::pair<std::string, uint32_t>> advertisedExtensions;
    extern const std::vector<std::string> blockedExtensions;
    extern const std::vector<std::string> implicitExtensions;

} // namespace openxr_api_layer
