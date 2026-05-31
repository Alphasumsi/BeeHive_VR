// BeeHive_VR — OpenXR API layer (XR_APILAYER_NOVENDOR_beehive)
//
// Step 3: multi-region atlas. The shared mapping carries a FrameSlot (describes
// the atlas texture) followed by an array of QuadSlot entries (one per sub-
// region: rect within the atlas, pose, size). Layer copies the whole atlas
// into the swapchain image once per frame, then emits one XrCompositionLayerQuad
// per QuadSlot — each with its own imageRect, pose, and size.
//
// FrameSlot is 40 bytes, QuadSlot is 76 bytes, MAX_QUADS = 8 → mapping = 648.
//
// Based on https://github.com/mbucchia/OpenXR-Layer-Template (MIT).
// Copyright(c) 2022-2023 Matthieu Bucchianeri.

#include "pch.h"

#include "layer.h"
#include <log.h>
#include <unordered_map>
#include <deque>
#include <array>

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
            HRESULT hr = m_appDevice->QueryInterface(IID_PPV_ARGS(m_appDevice1.ReleaseAndGetAddressOf()));
            if (FAILED(hr)) {
                Log(fmt::format("xrCreateSession: QueryInterface ID3D11Device1 failed hr=0x{:08x}\n",
                                (uint32_t)hr));
                m_appDevice = nullptr;
                return result;
            }

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
                m_appDevice1.Reset();
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
                Log(fmt::format("xrEndFrame: frame #{} cache={} \n",
                                m_frameCount, m_handleCache.size()));
            }

            XrFrameEndInfo chained = *frameEndInfo;
            std::vector<const XrCompositionLayerBaseHeader*> layers(
                chained.layers, chained.layers + chained.layerCount);

            // Sized to kMaxQuads up front so we can push stable pointers
            // (the layers vector keeps references into this storage).
            std::array<XrCompositionLayerQuad, kMaxQuads> quadStorage{};

            // EnsureSetup is idempotent — it keeps trying every frame until
            // Electron has populated the FrameSlot and our swapchain is built.
            if (EnsureSetup()) {
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

      private:
        // FrameSlot + QuadSlot layouts — MUST match app/src/ipc/shared-frame.ts
        // byte-for-byte. Both little-endian (Windows native).
        struct FrameSlot {              // 40 bytes
            uint64_t generation;
            uint32_t producerPid;
            uint32_t reserved;
            uint64_t ntHandle;
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

        // ---------- Setup ------------------------------------------------------
        // Idempotent: every xrEndFrame calls this until everything is ready.
        // Returns true once swapchain + space + first cached texture are in.
        bool EnsureSetup() {
            if (m_swapchainReady) return true;

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
                slot.ntHandle == 0 || !slot.width || !slot.height) {
                return false;
            }

            Log(fmt::format("setup: first slot gen={} pid={} h=0x{:x} {}x{} fmt={}\n",
                            slot.generation, slot.producerPid, slot.ntHandle,
                            slot.width, slot.height, slot.format));

            // (c) Open Electron's process once; we'll keep PROCESS_DUP_HANDLE for
            //     the duration of the session and use it for every cache miss.
            m_parentProcess = OpenProcess(PROCESS_DUP_HANDLE, FALSE, slot.producerPid);
            if (!m_parentProcess) {
                Log(fmt::format("setup: OpenProcess(pid={}) failed err={}\n",
                                slot.producerPid, GetLastError()));
                return false;
            }
            m_producerPid = slot.producerPid;
            m_texWidth    = slot.width;
            m_texHeight   = slot.height;
            m_texFormat   = (DXGI_FORMAT)slot.format;

            // (d) Seed the cache with the first handle we'll be using.
            if (!OpenAndCacheTexture(slot.ntHandle)) {
                CloseHandle(m_parentProcess); m_parentProcess = nullptr;
                return false;
            }

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

        // Opens a shared NT-HANDLE coming from Electron, resolves it to an
        // ID3D11Texture2D, and stores it in m_handleCache. Returns true on
        // success. After OpenSharedResource1 the duplicated handle can be
        // closed: the COM ptr holds the underlying resource alive.
        bool OpenAndCacheTexture(uint64_t handleVal) {
            HANDLE dup = nullptr;
            const BOOL ok = DuplicateHandle(m_parentProcess, (HANDLE)(uintptr_t)handleVal,
                                            GetCurrentProcess(), &dup, 0, FALSE,
                                            DUPLICATE_SAME_ACCESS);
            if (!ok || !dup) {
                Log(fmt::format("cache: DuplicateHandle(0x{:x}) failed err={}\n",
                                handleVal, GetLastError()));
                return false;
            }
            ComPtr<ID3D11Texture2D> tex;
            const HRESULT hr = m_appDevice1->OpenSharedResource1(dup, IID_PPV_ARGS(&tex));
            CloseHandle(dup);
            if (FAILED(hr)) {
                Log(fmt::format("cache: OpenSharedResource1(0x{:x}) hr=0x{:08x}\n",
                                handleVal, (uint32_t)hr));
                return false;
            }
            m_handleCache.emplace(handleVal, tex);
            m_cacheOrder.push_back(handleVal);

            // FIFO eviction: bound at kMaxCacheSize so we do not hold the
            // entire long-tail of Chromium's handles open forever. Evicting
            // releases the ComPtr ref, allowing the GPU resource to be freed
            // once Chromium's own ref is gone.
            while (m_handleCache.size() > kMaxCacheSize) {
                const uint64_t oldest = m_cacheOrder.front();
                m_cacheOrder.pop_front();
                m_handleCache.erase(oldest);
            }

            if (m_handleCache.size() <= 8 || m_handleCache.size() % 8 == 0) {
                Log(fmt::format("cache: opened 0x{:x} (size now {})\n",
                                handleVal, m_handleCache.size()));
            }
            return true;
        }

        // Reads the current FrameSlot and returns the cached texture for its
        // ntHandle (opening + caching if this is the first time we see it).
        // Returns nullptr if the slot is unpopulated or the open failed.
        ID3D11Texture2D* GetCurrentTexture() {
            if (!m_mapView) return nullptr;
            FrameSlot slot{};
            memcpy(&slot, m_mapView, sizeof(slot));
            if (!slot.ntHandle) return nullptr;

            auto it = m_handleCache.find(slot.ntHandle);
            if (it != m_handleCache.end()) return it->second.Get();

            if (!OpenAndCacheTexture(slot.ntHandle)) return nullptr;
            return m_handleCache[slot.ntHandle].Get();
        }

        void Teardown() {
            m_swapchainReady = false;
            if (m_swapchain != XR_NULL_HANDLE) {
                OpenXrApi::xrDestroySwapchain(m_swapchain);
                m_swapchain = XR_NULL_HANDLE;
            }
            m_swapchainTextures.clear();
            if (m_localSpace != XR_NULL_HANDLE) {
                OpenXrApi::xrDestroySpace(m_localSpace);
                m_localSpace = XR_NULL_HANDLE;
            }
            m_handleCache.clear();
            m_cacheOrder.clear();
            if (m_mapView)        { UnmapViewOfFile(m_mapView); m_mapView = nullptr; }
            if (m_mapping)        { CloseHandle(m_mapping);     m_mapping = nullptr; }
            if (m_parentProcess)  { CloseHandle(m_parentProcess); m_parentProcess = nullptr; }
            m_texWidth = m_texHeight = 0;
            m_loggedNoMapping = false;
            m_loggedFirstQuads = false;
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

            // Build one quad descriptor per visible slot.
            uint32_t written = 0;
            for (uint32_t i = 0; i < requested; ++i) {
                const QuadSlot& s = slots[i];
                if (!s.visible) continue;
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
        ComPtr<ID3D11Device1> m_appDevice1;
        ComPtr<ID3D11DeviceContext> m_appContext;

        // Frame stats.
        uint64_t m_frameCount{0};

        // Shared-memory IPC (opened once on first successful EnsureSetup).
        HANDLE m_mapping{nullptr};
        void*  m_mapView{nullptr};
        bool   m_loggedNoMapping{false};

        // Cached Electron process handle (PROCESS_DUP_HANDLE) for fast misses.
        HANDLE m_parentProcess{nullptr};
        DWORD  m_producerPid{0};

        // Per-handle texture cache. ComPtr keeps the underlying D3D11 resource
        // alive on our side; the duplicated NT handle is closed after open.
        // FIFO eviction at kMaxCacheSize prevents unbounded growth — Chromium
        // can allocate fresh slots as we let old ones go.
        static constexpr size_t kMaxCacheSize = 64;
        std::unordered_map<uint64_t, ComPtr<ID3D11Texture2D>> m_handleCache;
        std::deque<uint64_t> m_cacheOrder;

        // Quad pipeline state.
        bool m_swapchainReady{false};
        bool m_loggedAcquireFail{false};
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
