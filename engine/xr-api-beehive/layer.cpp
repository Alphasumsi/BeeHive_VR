// BeeHive_VR — OpenXR API layer (XR_APILAYER_NOVENDOR_beehive)
//
// Architecture: Electron renders the atlas into a cloaked top-level
// BrowserWindow on the desktop and publishes its HWND in shared memory. This
// layer captures that window through Windows.Graphics.Capture, copies the
// surface into a swapchain image, and emits one XrCompositionLayerQuad per
// sub-region (pose + size + atlas rect from the QuadSlot array).
//
// FrameSlot is 40 bytes, QuadSlot is 76 bytes, MAX_QUADS = 8 → mapping = 648.
//
// Based on https://github.com/mbucchia/OpenXR-Layer-Template (MIT).
// Copyright(c) 2022-2023 Matthieu Bucchianeri.

#include "pch.h"

#include "layer.h"
#include <log.h>
#include <utils/capture.h>
#include <array>
#include <cstring>
#include <cmath>

namespace openxr_api_layer {

    using namespace log;

    const std::vector<std::pair<std::string, uint32_t>> advertisedExtensions = {};
    const std::vector<std::string> blockedExtensions = {};
    const std::vector<std::string> implicitExtensions = {};

    // Poses live in QuadSlot now — driven by Electron, see app/src/main.ts.

    class OpenXrLayer : public openxr_api_layer::OpenXrApi {
      public:
        OpenXrLayer() = default;
        ~OpenXrLayer() = default;

        // ---------- xrCreateInstance: app filter only --------------------------
        XrResult xrCreateInstance(const XrInstanceCreateInfo* createInfo) override {
            if (createInfo->type != XR_TYPE_INSTANCE_CREATE_INFO) {
                return XR_ERROR_VALIDATION_FAILURE;
            }
            OpenXrApi::xrCreateInstance(createInfo);

            Log(fmt::format("Application: {}\n", createInfo->applicationInfo.applicationName));
            m_bypassApiLayer =
                strcmp(createInfo->applicationInfo.applicationName, "iRacingSim64DX11") != 0;
            if (m_bypassApiLayer) {
                Log(fmt::format("{} layer will be bypassed\n", LayerName));
                return XR_SUCCESS;
            }

            Log(fmt::format("{} active — POC 3b shared-texture quad\n", LayerName));
            // No OpenXR re-entry here. See project_no_openxr_in_xrcreateinstance.
            return XR_SUCCESS;
        }

        // ---------- xrCreateSession: pull D3D11 device out of binding chain ----
        XrResult xrCreateSession(XrInstance instance,
                                 const XrSessionCreateInfo* createInfo,
                                 XrSession* session) override {
            const XrResult result = OpenXrApi::xrCreateSession(instance, createInfo, session);
            if (XR_FAILED(result) || m_bypassApiLayer) return result;

            const XrBaseInStructure* entry = reinterpret_cast<const XrBaseInStructure*>(createInfo->next);
            while (entry) {
                if (entry->type == XR_TYPE_GRAPHICS_BINDING_D3D11_KHR) {
                    const auto* b = reinterpret_cast<const XrGraphicsBindingD3D11KHR*>(entry);
                    m_appDevice = b->device;
                    break;
                }
                entry = entry->next;
            }
            if (!m_appDevice) {
                Log("xrCreateSession: no D3D11 graphics binding found — quad disabled\n");
                return result;
            }

            m_appDevice->GetImmediateContext(m_appContext.ReleaseAndGetAddressOf());
            m_session = *session;
            Log("xrCreateSession: captured app D3D11 device, awaiting first xrEndFrame for setup\n");
            return result;
        }

        // ---------- xrDestroySession: tear down our resources ------------------
        XrResult xrDestroySession(XrSession session) override {
            if (session == m_session) {
                Teardown();
                m_session = XR_NULL_HANDLE;
                m_appDevice = nullptr;
                m_appContext.Reset();
            }
            return OpenXrApi::xrDestroySession(session);
        }

        // ---------- xrEndFrame: keep-trying setup + per-frame texture lookup ---
        XrResult xrEndFrame(XrSession session, const XrFrameEndInfo* frameEndInfo) override {
            if (frameEndInfo->type != XR_TYPE_FRAME_END_INFO) {
                return XR_ERROR_VALIDATION_FAILURE;
            }

            if (m_bypassApiLayer || session != m_session || !m_appDevice) {
                return OpenXrApi::xrEndFrame(session, frameEndInfo);
            }

            m_frameCount++;
            if (m_frameCount == 1) Log("xrEndFrame: first call\n");
            if (m_frameCount % 450 == 0) {
                Log(fmt::format("xrEndFrame: frame #{}\n", m_frameCount));
            }

            XrFrameEndInfo chained = *frameEndInfo;
            std::vector<const XrCompositionLayerBaseHeader*> layers(
                chained.layers, chained.layers + chained.layerCount);

            // Sized to kMaxQuads up front so we can push stable pointers
            // (the layers vector keeps references into this storage).
            std::array<XrCompositionLayerQuad, kMaxQuads> quadStorage{};

            // Place-in-VR input is wired one-shot on the first frame past the
            // setup holdoff. xrCreateActionSet/CreateAction would deadlock VDXR
            // out of xrCreateInstance (see project_no_openxr_in_xrcreateinstance);
            // doing it here on the game thread is safe.
            if (m_frameCount >= kSetupHoldoffFrames && !m_inputSetupTried) {
                m_inputSetupTried = true;
                SetupInput();
            }

            // EnsureSetup is idempotent — it keeps trying every frame until
            // Electron has populated the FrameSlot and our swapchain is built.
            if (EnsureSetup()) {
                if (m_inputInitialized && !m_actionSetsAttached) {
                    XrSessionActionSetsAttachInfo ai{XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO};
                    ai.countActionSets = 1;
                    ai.actionSets = &m_inputActionSet;
                    XrResult ar = OpenXrApi::xrAttachSessionActionSets(session, &ai);
                    m_actionSetsAttached = true;
                    Log(fmt::format("Place: self-attached action set → {}\n", (int)ar));
                }

                bool inputSynced = false;
                if (m_actionSetsAttached && m_inputInitialized) {
                    EnsureActionSpaces();
                    XrActiveActionSet aas{m_inputActionSet, XR_NULL_PATH};
                    XrActionsSyncInfo syncInfo{XR_TYPE_ACTIONS_SYNC_INFO};
                    syncInfo.countActiveActionSets = 1;
                    syncInfo.activeActionSets = &aas;
                    inputSynced =
                        OpenXrApi::xrSyncActions(session, &syncInfo) == XR_SUCCESS;
                }

                if (inputSynced) {
                    DrivePlaceMode(session, frameEndInfo->displayTime);
                }

                ID3D11Texture2D* currentTex = GetCurrentTexture();
                if (currentTex) {
                    const uint32_t n = RenderAtlasQuads(currentTex, quadStorage.data());
                    for (uint32_t i = 0; i < n; ++i) {
                        layers.push_back(
                            reinterpret_cast<const XrCompositionLayerBaseHeader*>(&quadStorage[i]));
                    }
                }
            }

            chained.layers = layers.data();
            chained.layerCount = (uint32_t)layers.size();
            return OpenXrApi::xrEndFrame(session, &chained);
        }

        // ---------- xrAttachSessionActionSets piggyback ------------------------
        // Defensive: iRacing is known not to attach, but if that changes our
        // self-attach in xrEndFrame would fail (attach is once-per-session). If
        // the app calls this first, append our set onto its list.
        XrResult xrAttachSessionActionSets(
            XrSession session, const XrSessionActionSetsAttachInfo* attachInfo) override {
            if (!m_bypassApiLayer && m_inputInitialized && !m_actionSetsAttached && attachInfo &&
                attachInfo->type == XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO &&
                m_inputActionSet != XR_NULL_HANDLE) {
                std::vector<XrActionSet> sets(attachInfo->actionSets,
                                              attachInfo->actionSets + attachInfo->countActionSets);
                sets.push_back(m_inputActionSet);
                XrSessionActionSetsAttachInfo ai = *attachInfo;
                ai.actionSets = sets.data();
                ai.countActionSets = (uint32_t)sets.size();
                XrResult r = OpenXrApi::xrAttachSessionActionSets(session, &ai);
                m_actionSetsAttached = true;
                Log(fmt::format("Place: piggyback-attached onto app ({} app sets) → {}\n",
                                attachInfo->countActionSets, (int)r));
                return r;
            }
            return OpenXrApi::xrAttachSessionActionSets(session, attachInfo);
        }

      private:
        // FrameSlot + QuadSlot layouts — MUST match app/src/ipc/shared-frame.ts
        // byte-for-byte. Both little-endian (Windows native).
        struct FrameSlot {              // 40 bytes
            uint64_t generation;
            uint32_t producerPid;
            uint32_t reserved;
            uint64_t hwnd;            // Electron BrowserWindow HWND for WGC
            uint32_t width;
            uint32_t height;
            uint32_t format;
            uint32_t quadCount;
        };
        static_assert(sizeof(FrameSlot) == 40, "FrameSlot must be exactly 40 bytes");

        struct QuadSlot {               // 76 bytes
            char     id[16];
            uint32_t rectX;
            uint32_t rectY;
            uint32_t rectW;
            uint32_t rectH;
            float    posX, posY, posZ;
            float    quatX, quatY, quatZ, quatW;
            float    sizeW, sizeH;
            uint32_t visible;
            uint32_t reserved;
        };
        static_assert(sizeof(QuadSlot) == 76, "QuadSlot must be exactly 76 bytes");

        static constexpr size_t kMaxQuads = 8;
        static constexpr size_t kMappingSize = sizeof(FrameSlot) + kMaxQuads * sizeof(QuadSlot);

        // ~half a second at 90 Hz — covers iRacing's loading-screen window
        // without noticeably delaying the appearance of the overlay in-cockpit.
        static constexpr uint64_t kSetupHoldoffFrames = 45;

        // PlaceOut layout: 8 (generation) + 16 (id) + 7*4 (floats) = 52, padded
        // to 96 for headroom (future opacity / source-id / flag fields).
        static constexpr size_t kPlaceOutSize = 96;

        // ---------- Setup ------------------------------------------------------
        // Idempotent: every xrEndFrame calls this until everything is ready.
        // Returns true once swapchain + space + first cached texture are in.
        //
        // Hold-off rationale: WGC bring-up (CreateForWindow + free-threaded
        // frame pool + StartCapture) is heavyweight WinRT/COM work. Running it
        // during iRacing's loading phase reliably wedges the loader (analogue
        // to the OpenXR-from-xrCreateInstance trap). The old HoneyOverlays
        // layer got the same effect for free because it waited on a named-pipe
        // snapshot from WPF before touching WGC; we publish into shared memory
        // immediately, so we have to hold off explicitly. ~half a second of
        // pass-through is enough for iRacing to finish loading the cockpit.
        bool EnsureSetup() {
            if (m_swapchainReady) return true;
            if (m_frameCount < kSetupHoldoffFrames) {
                if (!m_loggedHoldoff) {
                    Log(fmt::format("setup: holding off until xrEndFrame #{}\n",
                                    kSetupHoldoffFrames));
                    m_loggedHoldoff = true;
                }
                return false;
            }

            // (a) Open the shared-memory section Electron created. Keep it mapped
            //     for the lifetime of the session so we can read every frame.
            if (!m_mapping) {
                m_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\BeeHiveVR_Frame");
                if (!m_mapping) {
                    if (!m_loggedNoMapping) {
                        const DWORD e = GetLastError();
                        Log(fmt::format("setup: Local\\BeeHiveVR_Frame not opened yet (err={})\n", e));
                        m_loggedNoMapping = true;
                    }
                    return false;
                }
                m_mapView = MapViewOfFile(m_mapping, FILE_MAP_READ, 0, 0, kMappingSize);
                if (!m_mapView) {
                    Log(fmt::format("setup: MapViewOfFile err={}\n", GetLastError()));
                    CloseHandle(m_mapping); m_mapping = nullptr;
                    return false;
                }
                Log("setup: shared-memory mapping opened — waiting for first valid FrameSlot\n");
            }

            // (b) Peek at the slot. If Electron hasn't written yet, just wait.
            FrameSlot slot{};
            memcpy(&slot, m_mapView, sizeof(slot));
            if (slot.generation == 0 || slot.producerPid == 0 ||
                slot.hwnd == 0 || !slot.width || !slot.height) {
                return false;
            }

            // (c) Start WGC capture against Electron's window — ONCE. The
            //     ctor builds an interop D3D11 device wrapping our app device,
            //     creates a free-threaded frame pool, and starts the session.
            //     Re-entrancy guard: if EnsureSetup later returns false (e.g.
            //     WGC needs another frame to deliver), the same session keeps
            //     running on subsequent xrEndFrame calls.
            if (!m_captureWindow) {
                Log(fmt::format("setup: first slot gen={} pid={} hwnd=0x{:x} {}x{} fmt={}\n",
                                slot.generation, slot.producerPid, slot.hwnd,
                                slot.width, slot.height, slot.format));
                m_capturedHwnd = (HWND)(uintptr_t)slot.hwnd;
                try {
                    m_captureWindow =
                        std::make_unique<capture::CaptureWindowWinRT>(m_appDevice, m_capturedHwnd);
                } catch (const winrt::hresult_error& e) {
                    Log(fmt::format("setup: CaptureWindowWinRT ctor failed hr=0x{:08x} ({})\n",
                                    (uint32_t)e.code().value,
                                    winrt::to_string(e.message())));
                    m_capturedHwnd = nullptr;
                    return false;
                }
            }

            // (d) Wait for the first captured frame before sizing our swapchain.
            //     WGC reports the source size on the GraphicsCaptureItem but the
            //     first TryGetNextFrame can return nullptr for ~1-2 frames after
            //     StartCapture. Pull until we have something, then take that
            //     surface's actual dimensions.
            ID3D11Texture2D* firstFrame = m_captureWindow->getSurface();
            if (!firstFrame) {
                if (!m_loggedAwaitingFrame) {
                    Log("setup: WGC capture session created, awaiting first frame\n");
                    m_loggedAwaitingFrame = true;
                }
                return false;
            }
            D3D11_TEXTURE2D_DESC desc{};
            firstFrame->GetDesc(&desc);
            m_texWidth  = desc.Width;
            m_texHeight = desc.Height;
            m_texFormat = desc.Format;
            Log(fmt::format("setup: first WGC frame received {}x{} fmt={}\n",
                            m_texWidth, m_texHeight, (int)m_texFormat));

            // (e) ReferenceSpace LOCAL — fixed quad pose.
            XrReferenceSpaceCreateInfo rsi{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
            rsi.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
            rsi.poseInReferenceSpace.orientation.w = 1.0f;
            XrResult xr = OpenXrApi::xrCreateReferenceSpace(m_session, &rsi, &m_localSpace);
            if (XR_FAILED(xr) || m_localSpace == XR_NULL_HANDLE) {
                Log(fmt::format("setup: xrCreateReferenceSpace failed res={}\n", (int)xr));
                Teardown();
                return false;
            }

            // (f) Swapchain — same format as source so CopyResource is legal.
            XrSwapchainCreateInfo sci{XR_TYPE_SWAPCHAIN_CREATE_INFO};
            sci.usageFlags = XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
            sci.format     = (int64_t)m_texFormat;
            sci.width      = m_texWidth;
            sci.height     = m_texHeight;
            sci.sampleCount = 1; sci.faceCount = 1; sci.arraySize = 1; sci.mipCount = 1;
            xr = OpenXrApi::xrCreateSwapchain(m_session, &sci, &m_swapchain);
            if (XR_FAILED(xr)) {
                Log(fmt::format("setup: xrCreateSwapchain failed res={}\n", (int)xr));
                Teardown();
                return false;
            }

            uint32_t imageCount = 0;
            OpenXrApi::xrEnumerateSwapchainImages(m_swapchain, 0, &imageCount, nullptr);
            std::vector<XrSwapchainImageD3D11KHR> images(
                imageCount, XrSwapchainImageD3D11KHR{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
            OpenXrApi::xrEnumerateSwapchainImages(
                m_swapchain, imageCount, &imageCount,
                reinterpret_cast<XrSwapchainImageBaseHeader*>(images.data()));
            m_swapchainTextures.clear();
            for (auto& img : images) m_swapchainTextures.push_back(img.texture);

            Log(fmt::format("setup: swapchain {}x{} fmt={} imageCount={} — quad live\n",
                            m_texWidth, m_texHeight, (int)m_texFormat, imageCount));
            m_swapchainReady = true;
            return true;
        }

        // Returns the latest WGC-captured surface, or nullptr if the pool has
        // not produced a frame this cycle. The CaptureWindowWinRT caches the
        // last surface internally so consecutive calls without a fresh frame
        // still return the previous one — that is the desired behaviour for
        // VR (no holes, just a tiny re-show of the last frame).
        ID3D11Texture2D* GetCurrentTexture() {
            if (!m_captureWindow) return nullptr;
            return m_captureWindow->getSurface();
        }

        void Teardown() {
            m_swapchainReady = false;
            if (m_swapchain != XR_NULL_HANDLE) {
                OpenXrApi::xrDestroySwapchain(m_swapchain);
                m_swapchain = XR_NULL_HANDLE;
            }
            m_swapchainTextures.clear();
            for (int h = 0; h < 2; ++h) {
                if (m_aimSpace[h] != XR_NULL_HANDLE) {
                    OpenXrApi::xrDestroySpace(m_aimSpace[h]);
                    m_aimSpace[h] = XR_NULL_HANDLE;
                }
            }
            if (m_localSpace != XR_NULL_HANDLE) {
                OpenXrApi::xrDestroySpace(m_localSpace);
                m_localSpace = XR_NULL_HANDLE;
            }
            if (m_inputActionSet != XR_NULL_HANDLE) {
                OpenXrApi::xrDestroyActionSet(m_inputActionSet);
                m_inputActionSet = XR_NULL_HANDLE;
            }
            m_aimAction = XR_NULL_HANDLE;
            m_triggerAction = XR_NULL_HANDLE;
            m_inputSetupTried = false;
            m_inputInitialized = false;
            m_actionSetsAttached = false;
            m_actionSpacesCreated = false;
            m_grabHand = -1;
            m_captureWindow.reset();
            m_capturedHwnd = nullptr;
            if (m_placeOutView)    { UnmapViewOfFile(m_placeOutView);    m_placeOutView = nullptr; }
            if (m_placeOutMapping) { CloseHandle(m_placeOutMapping);     m_placeOutMapping = nullptr; }
            if (m_mapView)         { UnmapViewOfFile(m_mapView);         m_mapView = nullptr; }
            if (m_mapping)         { CloseHandle(m_mapping);              m_mapping = nullptr; }
            m_texWidth = m_texHeight = 0;
            m_loggedNoMapping = false;
            m_loggedAwaitingFrame = false;
            m_loggedHoldoff = false;
            m_loggedFirstQuads = false;
        }

        // ---------- Place-in-VR ------------------------------------------------
        // SetupInput is one-shot, called after the holdoff window has passed
        // (same reason as WGC init: heavy WinRT/COM/OpenXR work in the
        // xrEndFrame hot path during loading wedges iRacing's loader).
        void SetupInput() {
            XrActionSetCreateInfo asci{XR_TYPE_ACTION_SET_CREATE_INFO};
            strcpy_s(asci.actionSetName, "beehive_place");
            strcpy_s(asci.localizedActionSetName, "BeeHive Place-in-VR");
            asci.priority = 0;
            XrResult r = OpenXrApi::xrCreateActionSet(GetXrInstance(), &asci, &m_inputActionSet);
            if (r != XR_SUCCESS) {
                Log(fmt::format("Place: xrCreateActionSet failed ({})\n", (int)r));
                return;
            }

            auto strToPath = [&](const char* s) -> XrPath {
                XrPath p = XR_NULL_PATH;
                OpenXrApi::xrStringToPath(GetXrInstance(), s, &p);
                return p;
            };
            m_handPath[0] = strToPath("/user/hand/left");
            m_handPath[1] = strToPath("/user/hand/right");

            XrActionCreateInfo aimCi{XR_TYPE_ACTION_CREATE_INFO};
            aimCi.actionType = XR_ACTION_TYPE_POSE_INPUT;
            strcpy_s(aimCi.actionName, "aim_pose");
            strcpy_s(aimCi.localizedActionName, "Aim Pose");
            aimCi.countSubactionPaths = 2;
            aimCi.subactionPaths = m_handPath;
            r = OpenXrApi::xrCreateAction(m_inputActionSet, &aimCi, &m_aimAction);
            if (r != XR_SUCCESS) {
                Log(fmt::format("Place: xrCreateAction(aim) failed ({})\n", (int)r));
                return;
            }

            XrActionCreateInfo trgCi{XR_TYPE_ACTION_CREATE_INFO};
            trgCi.actionType = XR_ACTION_TYPE_BOOLEAN_INPUT;
            strcpy_s(trgCi.actionName, "trigger");
            strcpy_s(trgCi.localizedActionName, "Trigger");
            trgCi.countSubactionPaths = 2;
            trgCi.subactionPaths = m_handPath;
            r = OpenXrApi::xrCreateAction(m_inputActionSet, &trgCi, &m_triggerAction);
            if (r != XR_SUCCESS) {
                Log(fmt::format("Place: xrCreateAction(trigger) failed ({})\n", (int)r));
                return;
            }

            // Suggested bindings — boolean-trigger on a float path is fine,
            // the runtime thresholds. Profile coverage matches the old layer.
            auto suggest = [&](const char* profile,
                               const char* trgL, const char* trgR) -> bool {
                XrActionSuggestedBinding b[4] = {
                    {m_aimAction,     strToPath("/user/hand/left/input/aim/pose")},
                    {m_aimAction,     strToPath("/user/hand/right/input/aim/pose")},
                    {m_triggerAction, strToPath(trgL)},
                    {m_triggerAction, strToPath(trgR)},
                };
                XrInteractionProfileSuggestedBinding s{XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING};
                s.interactionProfile = strToPath(profile);
                s.countSuggestedBindings = 4;
                s.suggestedBindings = b;
                XrResult sr = OpenXrApi::xrSuggestInteractionProfileBindings(GetXrInstance(), &s);
                Log(fmt::format("Place: suggest \"{}\" → {}\n", profile, (int)sr));
                return sr == XR_SUCCESS;
            };
            suggest("/interaction_profiles/khr/simple_controller",
                    "/user/hand/left/input/select/click",
                    "/user/hand/right/input/select/click");
            suggest("/interaction_profiles/oculus/touch_controller",
                    "/user/hand/left/input/trigger/value",
                    "/user/hand/right/input/trigger/value");

            // PlaceOut-Mapping: layer publishes pose updates here, Electron
            // polls + forwards to WPF over the existing pipe. 96 bytes is
            // generous room for {generation, id[16], x, y, z, yaw, pitch,
            // scale, padding}.
            m_placeOutMapping = CreateFileMappingW(
                INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, kPlaceOutSize,
                L"Local\\BeeHiveVR_PlaceOut");
            if (m_placeOutMapping) {
                m_placeOutView = MapViewOfFile(m_placeOutMapping, FILE_MAP_WRITE,
                                               0, 0, kPlaceOutSize);
                if (m_placeOutView) {
                    std::memset(m_placeOutView, 0, kPlaceOutSize);
                    Log("Place: PlaceOut mapping ready (Local\\BeeHiveVR_PlaceOut)\n");
                } else {
                    Log(fmt::format("Place: MapViewOfFile(PlaceOut) err={}\n", GetLastError()));
                    CloseHandle(m_placeOutMapping); m_placeOutMapping = nullptr;
                }
            } else {
                Log(fmt::format("Place: CreateFileMappingW(PlaceOut) err={}\n", GetLastError()));
            }

            m_inputInitialized = true;
            Log("Place: action set + bindings ready (self-attach pending first frame)\n");
        }

        void EnsureActionSpaces() {
            if (m_actionSpacesCreated || m_aimAction == XR_NULL_HANDLE) return;
            m_actionSpacesCreated = true;
            for (int h = 0; h < 2; ++h) {
                XrActionSpaceCreateInfo si{XR_TYPE_ACTION_SPACE_CREATE_INFO};
                si.action = m_aimAction;
                si.subactionPath = m_handPath[h];
                si.poseInActionSpace.orientation.w = 1.0f;
                XrResult cr =
                    OpenXrApi::xrCreateActionSpace(m_session, &si, &m_aimSpace[h]);
                if (cr != XR_SUCCESS) {
                    Log(fmt::format("Place: xrCreateActionSpace[{}] → {}\n", h, (int)cr));
                }
            }
            Log("Place: action spaces created\n");
        }

        // Closest-quad grab: when no grab is active and a trigger press lands,
        // pick the visible quad nearest the controller and grab it. While grab
        // is active, modifier matrix drives the delta (kein=XY, CTRL=Z, SHIFT=
        // Scale, CTRL+SHIFT=Yaw/Pitch). Local pose override in
        // RenderAtlasQuads keeps the visual in sync with the hand without
        // waiting for the Electron round-trip.
        void DrivePlaceMode(XrSession session, XrTime displayTime) {
            auto triggerDown = [&](int h) -> bool {
                XrActionStateGetInfo gi{XR_TYPE_ACTION_STATE_GET_INFO};
                gi.action = m_triggerAction;
                gi.subactionPath = m_handPath[h];
                XrActionStateBoolean tb{XR_TYPE_ACTION_STATE_BOOLEAN};
                if (OpenXrApi::xrGetActionStateBoolean(session, &gi, &tb) != XR_SUCCESS) {
                    return false;
                }
                return tb.isActive && tb.currentState != XR_FALSE;
            };
            auto ctrlPose = [&](int h, XrVector3f& pos, XrQuaternionf& ori) -> bool {
                if (m_aimSpace[h] == XR_NULL_HANDLE) return false;
                XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
                if (OpenXrApi::xrLocateSpace(m_aimSpace[h], m_localSpace,
                                             displayTime, &loc) != XR_SUCCESS) {
                    return false;
                }
                const XrSpaceLocationFlags need =
                    XR_SPACE_LOCATION_POSITION_VALID_BIT |
                    XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
                if ((loc.locationFlags & need) != need) return false;
                pos = loc.pose.position;
                ori = loc.pose.orientation;
                return true;
            };

            // Snapshot current frame so we can resolve quad poses.
            FrameSlot frame{};
            std::array<QuadSlot, kMaxQuads> slots{};
            std::memcpy(&frame, m_mapView, sizeof(frame));
            const uint32_t n = std::min<uint32_t>(frame.quadCount, kMaxQuads);
            if (n > 0) {
                std::memcpy(slots.data(),
                            (const std::byte*)m_mapView + sizeof(FrameSlot),
                            n * sizeof(QuadSlot));
            }

            // --- Grab active: drag the locked quad ---------------------------
            if (m_grabHand != -1) {
                if (!triggerDown(m_grabHand)) {
                    Log(fmt::format("Place: released id=\"{}\"\n", m_grabTargetId));
                    m_grabHand = -1;
                    return;
                }
                XrVector3f cpos{};
                XrQuaternionf cori{};
                if (!ctrlPose(m_grabHand, cpos, cori)) return;

                const bool kCtrl  = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                const bool kShift = (GetAsyncKeyState(VK_SHIFT)   & 0x8000) != 0;
                int mod = -1;                   // XY
                if (kCtrl && kShift) mod = 3;   // Yaw/Pitch (wrist)
                else if (kCtrl)      mod = 0;   // Z
                else if (kShift)     mod = 1;   // Scale

                float curYaw = 0.f, curPitch = 0.f;
                QuatToYawPitchDeg(cori, curYaw, curPitch);

                if (mod != m_grabModifier) {
                    m_grabModifier = mod;
                    m_ctrlRef = cpos;
                    m_ctrlYawRef   = curYaw;
                    m_ctrlPitchRef = curPitch;
                    m_dragPosXRef = m_dragPosX;
                    m_dragPosYRef = m_dragPosY;
                    m_dragPosZRef = m_dragPosZ;
                    m_dragSizeWRef = m_dragSizeW;
                    m_dragSizeHRef = m_dragSizeH;
                    m_dragYawRef   = m_dragYawDeg;
                    m_dragPitchRef = m_dragPitchDeg;
                }

                constexpr float K_SCALE = 1.5f;             // multiplicative per meter forward
                const float fwd = -(cpos.z - m_ctrlRef.z);  // hand toward target = positive

                switch (mod) {
                case -1:
                    m_dragPosX = m_dragPosXRef + (cpos.x - m_ctrlRef.x);
                    m_dragPosY = m_dragPosYRef + (cpos.y - m_ctrlRef.y);
                    break;
                case 0:
                    m_dragPosZ = m_dragPosZRef + (cpos.z - m_ctrlRef.z);
                    break;
                case 1: {
                    const float factor = std::clamp(1.f + fwd * K_SCALE, 0.05f, 20.f);
                    m_dragSizeW = std::clamp(m_dragSizeWRef * factor, 0.02f, 5.f);
                    m_dragSizeH = std::clamp(m_dragSizeHRef * factor, 0.02f, 5.f);
                    break;
                }
                case 3:
                    m_dragYawDeg   = m_dragYawRef   + (curYaw   - m_ctrlYawRef);
                    m_dragPitchDeg = m_dragPitchRef + (curPitch - m_ctrlPitchRef);
                    QuatFromYawPitchDeg(m_dragYawDeg, m_dragPitchDeg,
                                        m_dragQuatX, m_dragQuatY, m_dragQuatZ, m_dragQuatW);
                    break;
                }
                PublishPlaceOut();
                return;
            }

            // --- No grab: closest visible quad on trigger press -------------
            int trigHand = -1;
            for (int h = 0; h < 2; ++h) if (triggerDown(h)) { trigHand = h; break; }
            if (trigHand < 0) return;

            XrVector3f hand{};
            XrQuaternionf handOri{};
            if (!ctrlPose(trigHand, hand, handOri)) return;

            int bestIdx = -1;
            float bestD2 = 1e9f;
            for (uint32_t i = 0; i < n; ++i) {
                if (!slots[i].visible) continue;
                const float dx = slots[i].posX - hand.x;
                const float dy = slots[i].posY - hand.y;
                const float dz = slots[i].posZ - hand.z;
                const float d2 = dx*dx + dy*dy + dz*dz;
                if (d2 < bestD2) { bestD2 = d2; bestIdx = (int)i; }
            }
            if (bestIdx < 0) return;

            const QuadSlot& s = slots[bestIdx];
            m_grabHand = trigHand;
            std::memcpy(m_grabTargetId, s.id, 16);
            m_grabModifier = -2;                  // forces rebaseline on first drag frame
            m_dragPosX = s.posX; m_dragPosY = s.posY; m_dragPosZ = s.posZ;
            m_dragSizeW = s.sizeW; m_dragSizeH = s.sizeH;
            m_dragQuatX = s.quatX; m_dragQuatY = s.quatY;
            m_dragQuatZ = s.quatZ; m_dragQuatW = s.quatW;
            QuatToYawPitchDeg({s.quatX, s.quatY, s.quatZ, s.quatW},
                              m_dragYawDeg, m_dragPitchDeg);
            char idLog[17] = {}; std::memcpy(idLog, m_grabTargetId, 16);
            Log(fmt::format("Place: grabbed id=\"{}\" hand={} d={:.3f}m\n",
                            idLog, trigHand == 0 ? "L" : "R", std::sqrt(bestD2)));
            PublishPlaceOut();
        }

        // Writes the current drag state to the PlaceOut mapping. Generation
        // increments every call so Electron can detect deltas cheaply. id is
        // copied raw (16 char), no terminator guarantee — Electron should
        // truncate at the first NUL.
        void PublishPlaceOut() {
            if (!m_placeOutView) return;
            std::byte* p = (std::byte*)m_placeOutView;
            ++m_placeOutGen;
            std::memcpy(p + 0,  &m_placeOutGen, 8);
            std::memcpy(p + 8,  m_grabTargetId, 16);
            float f[7] = { m_dragPosX, m_dragPosY, m_dragPosZ,
                           m_dragYawDeg, m_dragPitchDeg, m_dragSizeW, m_dragSizeH };
            std::memcpy(p + 24, f, sizeof(f));
        }

        // Quaternion <-> yaw/pitch helpers. Convention: q = qYaw * qPitch
        // (yaw around Y, pitch around X). Mirrors the old layer.
        static void QuatToYawPitchDeg(const XrQuaternionf& q, float& yawDeg, float& pitchDeg) {
            constexpr float kRad2Deg = 180.f / 3.14159265358979323846f;
            yawDeg = std::atan2(2.f * (q.w * q.y + q.x * q.z),
                                1.f - 2.f * (q.x * q.x + q.y * q.y)) * kRad2Deg;
            float sp = 2.f * (q.w * q.x - q.y * q.z);
            sp = std::clamp(sp, -1.f, 1.f);
            pitchDeg = std::asin(sp) * kRad2Deg;
        }
        static void QuatFromYawPitchDeg(float yawDeg, float pitchDeg,
                                        float& x, float& y, float& z, float& w) {
            constexpr float kDeg2Rad = 3.14159265358979323846f / 180.f;
            const float y2 = yawDeg * kDeg2Rad * 0.5f;
            const float p2 = pitchDeg * kDeg2Rad * 0.5f;
            const float cy = std::cos(y2), sy = std::sin(y2);
            const float cx = std::cos(p2), sx = std::sin(p2);
            w =  cy * cx;
            x =  cy * sx;
            y =  sy * cx;
            z = -sy * sx;
        }

        // ---------- Per-frame --------------------------------------------------
        // Copy the atlas once, then emit one XrCompositionLayerQuad per visible
        // QuadSlot. Returns the number of quads written to outQuads (≤ kMaxQuads).
        uint32_t RenderAtlasQuads(ID3D11Texture2D* sourceTex, XrCompositionLayerQuad* outQuads) {
            // Read the slot snapshot once: handle/quadCount/array.
            FrameSlot frame{};
            std::array<QuadSlot, kMaxQuads> slots{};
            memcpy(&frame, m_mapView, sizeof(frame));
            const uint32_t requested = std::min<uint32_t>(frame.quadCount, kMaxQuads);
            if (requested > 0) {
                memcpy(slots.data(),
                       (const std::byte*)m_mapView + sizeof(FrameSlot),
                       requested * sizeof(QuadSlot));
            }

            // Acquire / wait the next swapchain image, copy atlas, release.
            XrSwapchainImageAcquireInfo ai{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
            uint32_t imageIndex = 0;
            XrResult xr = OpenXrApi::xrAcquireSwapchainImage(m_swapchain, &ai, &imageIndex);
            if (XR_FAILED(xr)) {
                if (!m_loggedAcquireFail) {
                    Log(fmt::format("frame: xrAcquireSwapchainImage res={}\n", (int)xr));
                    m_loggedAcquireFail = true;
                }
                return 0;
            }
            XrSwapchainImageWaitInfo wi{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO};
            wi.timeout = XR_INFINITE_DURATION;
            xr = OpenXrApi::xrWaitSwapchainImage(m_swapchain, &wi);
            if (XR_FAILED(xr)) {
                Log(fmt::format("frame: xrWaitSwapchainImage res={}\n", (int)xr));
                return 0;
            }

            ID3D11Texture2D* dst = m_swapchainTextures[imageIndex];
            m_appContext->CopyResource(dst, sourceTex);

            XrSwapchainImageReleaseInfo ri{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
            xr = OpenXrApi::xrReleaseSwapchainImage(m_swapchain, &ri);
            if (XR_FAILED(xr)) {
                Log(fmt::format("frame: xrReleaseSwapchainImage res={}\n", (int)xr));
                return 0;
            }

            // Build one quad descriptor per visible slot. While a grab is
            // active we apply the layer-local pose override for that id so the
            // visual matches the controller motion immediately (the round-trip
            // through Electron + WPF + back into the FrameSlot is too laggy
            // for direct-manipulation UX).
            uint32_t written = 0;
            for (uint32_t i = 0; i < requested; ++i) {
                QuadSlot s = slots[i];
                if (!s.visible) continue;
                if (m_grabHand != -1 && std::strncmp(s.id, m_grabTargetId, 16) == 0) {
                    s.posX = m_dragPosX; s.posY = m_dragPosY; s.posZ = m_dragPosZ;
                    s.quatX = m_dragQuatX; s.quatY = m_dragQuatY;
                    s.quatZ = m_dragQuatZ; s.quatW = m_dragQuatW;
                    s.sizeW = m_dragSizeW; s.sizeH = m_dragSizeH;
                }
                XrCompositionLayerQuad& q = outQuads[written++];
                q.type = XR_TYPE_COMPOSITION_LAYER_QUAD;
                q.next = nullptr;
                q.layerFlags = XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT;
                q.space = m_localSpace;
                q.eyeVisibility = XR_EYE_VISIBILITY_BOTH;
                q.subImage.swapchain = m_swapchain;
                q.subImage.imageRect = {{(int32_t)s.rectX, (int32_t)s.rectY},
                                        {(int32_t)s.rectW, (int32_t)s.rectH}};
                q.subImage.imageArrayIndex = 0;
                q.pose.position    = {s.posX, s.posY, s.posZ};
                q.pose.orientation = {s.quatX, s.quatY, s.quatZ, s.quatW};
                q.size             = {s.sizeW, s.sizeH};
            }

            // Once-per-launch diagnostic so we see the layout the layer is honoring.
            if (!m_loggedFirstQuads && written > 0) {
                m_loggedFirstQuads = true;
                Log(fmt::format("atlas: rendering {} quad(s), atlas={}x{}\n",
                                written, frame.width, frame.height));
                for (uint32_t i = 0; i < requested; ++i) {
                    const QuadSlot& s = slots[i];
                    char id[17] = {}; memcpy(id, s.id, 16);
                    Log(fmt::format("  quad[{}] id='{}' rect=({},{},{},{}) "
                                    "pos=({:.2f},{:.2f},{:.2f}) size=({:.2f}x{:.2f})m visible={}\n",
                                    i, id, s.rectX, s.rectY, s.rectW, s.rectH,
                                    s.posX, s.posY, s.posZ, s.sizeW, s.sizeH, s.visible));
                }
            }
            return written;
        }

      private:
        // App / runtime state.
        bool m_bypassApiLayer{false};
        XrSession m_session{XR_NULL_HANDLE};
        ID3D11Device* m_appDevice{nullptr};            // app-owned, not refcounted by us
        ComPtr<ID3D11DeviceContext> m_appContext;

        // Frame stats.
        uint64_t m_frameCount{0};

        // Shared-memory IPC (opened once on first successful EnsureSetup).
        HANDLE m_mapping{nullptr};
        void*  m_mapView{nullptr};
        bool   m_loggedNoMapping{false};

        // WGC capture against Electron's BrowserWindow HWND. The session
        // started by CaptureWindowWinRT owns a free-threaded frame pool and
        // produces ID3D11Texture2D surfaces on the device we hand in.
        std::unique_ptr<capture::ICaptureWindow> m_captureWindow;
        HWND m_capturedHwnd{nullptr};

        // Place-in-VR.
        XrActionSet m_inputActionSet{XR_NULL_HANDLE};
        XrAction    m_aimAction{XR_NULL_HANDLE};
        XrAction    m_triggerAction{XR_NULL_HANDLE};
        XrPath      m_handPath[2]{XR_NULL_PATH, XR_NULL_PATH};
        XrSpace     m_aimSpace[2]{XR_NULL_HANDLE, XR_NULL_HANDLE};
        bool        m_inputSetupTried{false};
        bool        m_inputInitialized{false};
        bool        m_actionSetsAttached{false};
        bool        m_actionSpacesCreated{false};

        // Grab + drag state (layer-authoritative while grab is active).
        int         m_grabHand{-1};
        char        m_grabTargetId[16]{};
        int         m_grabModifier{-2};        // -2 = needs rebaseline
        XrVector3f  m_ctrlRef{};
        float       m_ctrlYawRef{0.f}, m_ctrlPitchRef{0.f};
        float       m_dragPosX{0.f}, m_dragPosY{0.f}, m_dragPosZ{0.f};
        float       m_dragPosXRef{0.f}, m_dragPosYRef{0.f}, m_dragPosZRef{0.f};
        float       m_dragSizeW{0.f}, m_dragSizeH{0.f};
        float       m_dragSizeWRef{0.f}, m_dragSizeHRef{0.f};
        float       m_dragYawDeg{0.f}, m_dragPitchDeg{0.f};
        float       m_dragYawRef{0.f}, m_dragPitchRef{0.f};
        float       m_dragQuatX{0.f}, m_dragQuatY{0.f}, m_dragQuatZ{0.f}, m_dragQuatW{1.f};

        // PlaceOut shared-memory section (layer → Electron). 96 bytes.
        HANDLE      m_placeOutMapping{nullptr};
        void*       m_placeOutView{nullptr};
        uint64_t    m_placeOutGen{0};

        // Quad pipeline state.
        bool m_swapchainReady{false};
        bool m_loggedAcquireFail{false};
        bool m_loggedAwaitingFrame{false};
        bool m_loggedHoldoff{false};
        bool m_loggedFirstQuads{false};
        UINT m_texWidth{0};
        UINT m_texHeight{0};
        DXGI_FORMAT m_texFormat{DXGI_FORMAT_UNKNOWN};
        XrSpace m_localSpace{XR_NULL_HANDLE};
        XrSwapchain m_swapchain{XR_NULL_HANDLE};
        std::vector<ID3D11Texture2D*> m_swapchainTextures; // runtime-owned
    };

    OpenXrApi* GetInstance() {
        if (!g_instance) {
            g_instance = std::make_unique<OpenXrLayer>();
        }
        return g_instance.get();
    }

} // namespace openxr_api_layer

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
        TraceLoggingRegister(openxr_api_layer::log::g_traceProvider);
        break;
    case DLL_PROCESS_DETACH:
        TraceLoggingUnregister(openxr_api_layer::log::g_traceProvider);
        break;
    case DLL_THREAD_ATTACH:
    case DLL_THREAD_DETACH:
        break;
    }
    return TRUE;
}
