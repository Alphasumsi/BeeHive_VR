// capture-host — D1 (8.7.2026): WGC-Capture + Chroma-Compose in einem EIGENEN Prozess
// statt in iRacings. Hintergrund: DWM blittet Capture-Frames in Pool-Surfaces, deren
// WDDM-Allokationen dem erzeugenden PROZESS gehören — lief die Capture im iRacing-
// Prozess, stallte iRacings GPU-Konto (~6s-Freezes trotz C/C2-Drosseln). Hier gehören
// die Allokationen capture-host → iRacings Render-Queue hat keinerlei Sync-/Residency-
// Berührung mehr mit der Capture (OpenKneeboard-Architektur).
//
// Ablauf pro Tick (BEEHIVE_COMPOSE_HZ, Default 60):
//   FrameSlot lesen (Atlas = Writer) → WGC-Surface pullen (C/C2-gedrosselt) →
//   Chroma-Compose aller sichtbaren Quads in Ring-Buffer j=(latest+1)%3 →
//   Fence-Signal → TexOut-SHM publizieren (Seqlock). Der Layer in iRacing kopiert
//   nur noch fertige Buffer nach CPU-seitigem Fence-Check — nie blockierend.
//
// Handle-Übergabe: Der Helper dupliziert Textur-/Fence-Handles IN den Zielprozess
// (HL.layerPid) — der Layer macht selbst KEIN OpenProcess (crasht aus iRacings
// Adressraum, Lehre 17.6.). targetPid in TexOut = Verifikation für den Layer.

#include "pch.h"

#include <atomic>
#include <cstdarg>
#include <cstddef>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <vector>

#include "beehive_shm.h"
#include "utils/capture.h"
#include "compose.h"

namespace {

    using namespace beehive::shm;

    // ---------------- Logging (Konvention: %LOCALAPPDATA%\BeeHive_VR\logs, Lokalzeit,
    // 3-MB-Rotation auf .old — wie engine.log/atlas.log, Cross-Ref-tauglich) ----------

    std::filesystem::path g_logPath;
    std::mutex g_logMutex;
    constexpr uintmax_t kLogMaxBytes = 3 * 1024 * 1024;

    void InitLog() {
        const char* lad = std::getenv("LOCALAPPDATA");
        std::filesystem::path dir = lad ? std::filesystem::path(lad) : std::filesystem::path(".");
        dir /= "BeeHive_VR";
        std::error_code ec;
        std::filesystem::create_directories(dir / "logs", ec);
        g_logPath = dir / "logs" / "capture-host.log";
    }

    void Log(const std::string& msg) {
        std::lock_guard<std::mutex> lock(g_logMutex);
        std::error_code ec;
        if (std::filesystem::exists(g_logPath, ec) &&
            std::filesystem::file_size(g_logPath, ec) > kLogMaxBytes) {
            auto old = g_logPath;
            old += ".old";
            std::filesystem::remove(old, ec);
            std::filesystem::rename(g_logPath, old, ec);
        }
        std::time_t t = std::time(nullptr);
        std::tm tm{};
        localtime_s(&tm, &t);
        char ts[32];
        std::strftime(ts, sizeof(ts), "%Y-%m-%d %H:%M:%S ", &tm);
        std::ofstream f(g_logPath, std::ios::app);
        if (f.is_open()) f << ts << msg << "\n";
        std::printf("%s%s\n", ts, msg.c_str()); // Konsole (manuelle Test-Läufe Phase 1-3)
    }

    std::string Fmt(const char* fmt, ...) {
        char buf[512];
        va_list args;
        va_start(args, fmt);
        vsnprintf(buf, sizeof(buf), fmt, args);
        va_end(args);
        return buf;
    }

    // ---------------- FrameSlot-SHM (read-only Mitleser; Atlas bleibt einziger Writer) --

    HANDLE g_frameMapping = nullptr;
    const uint8_t* g_frameView = nullptr;

    bool OpenFrameShm() {
        g_frameMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kFrameMappingName);
        if (!g_frameMapping) return false;
        g_frameView = static_cast<const uint8_t*>(
            MapViewOfFile(g_frameMapping, FILE_MAP_READ, 0, 0, kFrameMappingSize));
        if (!g_frameView) {
            CloseHandle(g_frameMapping);
            g_frameMapping = nullptr;
            return false;
        }
        return true;
    }

    // Seqlock-Doppelread (Tearing-Schutz, gleiche Konvention wie der Layer seit 10.6.).
    bool ReadFrameStable(FrameSlot& outFrame, QuadSlot* outSlots) {
        if (!g_frameView) return false;
        FrameSlot a{}, b{};
        std::memcpy(&a, g_frameView, sizeof(a));
        const uint32_t n = (a.quadCount < kMaxQuads) ? a.quadCount : kMaxQuads;
        if (outSlots && n > 0)
            std::memcpy(outSlots, g_frameView + sizeof(FrameSlot), n * sizeof(QuadSlot));
        std::memcpy(&b, g_frameView, sizeof(b));
        if (a.generation != b.generation) return false;
        outFrame = a;
        return true;
    }

    // ---------------- HL-SHM (Layer = Writer; hier read-only; fehlt → Defaults) --------

    HANDLE g_hlMapping = nullptr;
    const uint8_t* g_hlView = nullptr;

    bool TryOpenHlShm() {
        if (g_hlView) return true;
        g_hlMapping = OpenFileMappingW(FILE_MAP_READ, FALSE, kHighlightMappingName);
        if (!g_hlMapping) return false;
        g_hlView = static_cast<const uint8_t*>(
            MapViewOfFile(g_hlMapping, FILE_MAP_READ, 0, 0, sizeof(HighlightSlot)));
        if (!g_hlView) {
            CloseHandle(g_hlMapping);
            g_hlMapping = nullptr;
            return false;
        }
        return true;
    }

    void ReadHlStable(HighlightSlot& out) {
        out = HighlightSlot{}; // Defaults: kein Hover/Grab, layerPid=0
        if (!g_hlView) return;
        HighlightSlot a{}, b{};
        std::memcpy(&a, g_hlView, sizeof(a));
        std::memcpy(&b, g_hlView, sizeof(b));
        if (a.generation == b.generation) out = a;
    }

    // ---------------- AtlasTexIn-SHM (D2, OSR-Input) -----------------------------------
    // Atlas = Writer des Head (Seqlock); wir schreiben NUR consumedFrameCounter zurück
    // (Release-Rückmeldung). FILE_MAP_WRITE gewährt Lesen UND Schreiben.

    HANDLE g_atlasTexInMapping = nullptr;
    uint8_t* g_atlasTexInView = nullptr;

    bool TryOpenAtlasTexInShm() {
        if (g_atlasTexInView) return true;
        g_atlasTexInMapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, kAtlasTexInMappingName);
        if (!g_atlasTexInMapping) return false;
        g_atlasTexInView = static_cast<uint8_t*>(
            MapViewOfFile(g_atlasTexInMapping, FILE_MAP_WRITE, 0, 0, sizeof(AtlasTexIn)));
        if (!g_atlasTexInView) {
            CloseHandle(g_atlasTexInMapping);
            g_atlasTexInMapping = nullptr;
            return false;
        }
        return true;
    }

    bool ReadAtlasTexInStable(AtlasTexIn& out) {
        if (!g_atlasTexInView) return false;
        AtlasTexIn a{}, b{};
        std::memcpy(&a, g_atlasTexInView, sizeof(a));
        std::memcpy(&b, g_atlasTexInView, sizeof(b));
        if (a.generation != b.generation) return false;
        out = a;
        return true;
    }

    void WriteConsumed(uint64_t fc) {
        if (!g_atlasTexInView) return;
        std::memcpy(g_atlasTexInView + offsetof(AtlasTexIn, consumedFrameCounter), &fc, sizeof(fc));
    }

    // OpenProcess-Cache für die Handle-Duplikation aus dem Atlas-Prozess.
    HANDLE g_atlasProc = nullptr;
    uint32_t g_atlasProcPid = 0;

    // Zieht den prozess-lokalen Atlas-NT-Handle per DuplicateHandle zu uns und öffnet die
    // Shared-Textur. Richtung Atlas→capture-host ist unkritisch (wir sind NICHT im
    // iRacing-Adressraum, anders als der Layer — [[project_no_process_enum_in_layer]]).
    // Der duplizierte Handle wird nach OpenSharedResource1 sofort geschlossen (die
    // Ressource hält ihre eigene Referenz).
    bool OpenAtlasTexture(ID3D11Device5* dev, const AtlasTexIn& in,
                          ComPtr<ID3D11Texture2D>& out) {
        if (in.atlasPid != g_atlasProcPid || !g_atlasProc) {
            if (g_atlasProc) { CloseHandle(g_atlasProc); g_atlasProc = nullptr; }
            g_atlasProc = OpenProcess(PROCESS_DUP_HANDLE, FALSE, in.atlasPid);
            g_atlasProcPid = g_atlasProc ? in.atlasPid : 0;
            if (!g_atlasProc) {
                Log(Fmt("OpenAtlasTexture: OpenProcess(pid=%lu) err=%lu",
                        in.atlasPid, GetLastError()));
                return false;
            }
        }
        HANDLE local = nullptr;
        if (!DuplicateHandle(g_atlasProc, (HANDLE)(uintptr_t)in.ntHandle, GetCurrentProcess(),
                             &local, 0, FALSE, DUPLICATE_SAME_ACCESS)) {
            Log(Fmt("OpenAtlasTexture: DuplicateHandle err=%lu", GetLastError()));
            return false;
        }
        ComPtr<ID3D11Texture2D> tex;
        HRESULT hr = dev->OpenSharedResource1(local, IID_PPV_ARGS(&tex));
        CloseHandle(local);
        if (FAILED(hr)) {
            Log(Fmt("OpenAtlasTexture: OpenSharedResource1 hr=0x%08x", (uint32_t)hr));
            return false;
        }
        out = std::move(tex);
        return true;
    }

    // ---------------- TexOut-SHM (wir = einziger Writer; Seqlock-Publish) --------------

    HANDLE g_texOutMapping = nullptr;
    uint8_t* g_texOutView = nullptr;
    TexOutSlot g_texOut{}; // lokales Abbild; Publish kopiert + gen++

    bool CreateTexOutShm() {
        g_texOutMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                             0, sizeof(TexOutSlot), kTexOutMappingName);
        if (!g_texOutMapping) return false;
        g_texOutView = static_cast<uint8_t*>(
            MapViewOfFile(g_texOutMapping, FILE_MAP_WRITE, 0, 0, sizeof(TexOutSlot)));
        return g_texOutView != nullptr;
    }

    void PublishTexOut() {
        // Payload zuerst, Barrier, DANN generation — Reader (Layer) doppel-liest.
        g_texOut.generation++;
        TexOutSlot snap = g_texOut;
        const uint64_t gen = snap.generation;
        snap.generation = gen - 1; // Payload mit alter gen schreiben…
        std::memcpy(g_texOutView, &snap, sizeof(snap));
        MemoryBarrier();
        std::memcpy(g_texOutView, &gen, sizeof(gen)); // …dann atomar die neue gen
    }

    // ---------------- Shared-Texture-Ring + Fence --------------------------------------

    // D3D11 verlangt für NT-Handle-Sharing die Kombination SHARED_NTHANDLE|SHARED_KEYEDMUTEX
    // (NTHANDLE alleine = E_INVALIDARG, Phase-2-Test 8.7.) — und UAV auf die Shared-Tex
    // entfällt damit. Deshalb der Plan-Fallback: Compose in ein PRIVATES Intermediate
    // (UAV, wie im alten Layer-Pfad), dann CopyResource in den Shared-Ring — die Extra-
    // Copy liegt im Helper-Prozess, harmlos. Keyed-Mutex wird beidseitig strikt
    // NON-BLOCKING benutzt (AcquireSync(0, timeout=0)); der Fence bleibt zusätzlich als
    // CPU-seitiger „Buffer j ist fertig"-Check für den Layer (Gürtel + Hosenträger).
    struct SharedRing {
        ComPtr<ID3D11Texture2D> tex[kTexRingBuffers];
        ComPtr<IDXGIKeyedMutex> mutex[kTexRingBuffers];
        HANDLE localHandle[kTexRingBuffers]{}; // Helper-lokale NT-Handles
        ComPtr<ID3D11Texture2D> intermediate;  // privates Compose-Ziel (UAV-bindbar)
        ComPtr<ID3D11UnorderedAccessView> intermediateUAV;
        uint32_t width = 0, height = 0;

        void reset() {
            for (uint32_t i = 0; i < kTexRingBuffers; ++i) {
                if (localHandle[i]) CloseHandle(localHandle[i]);
                localHandle[i] = nullptr;
                mutex[i].Reset();
                tex[i].Reset();
            }
            intermediateUAV.Reset();
            intermediate.Reset();
            width = height = 0;
        }
    };

    bool CreateRing(ID3D11Device* device, uint32_t w, uint32_t h, SharedRing& ring,
                    std::string& err) {
        ring.reset();
        for (uint32_t i = 0; i < kTexRingBuffers; ++i) {
            D3D11_TEXTURE2D_DESC td{};
            td.Width            = w;
            td.Height           = h;
            td.MipLevels        = 1;
            td.ArraySize        = 1;
            td.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
            td.SampleDesc.Count = 1;
            td.Usage            = D3D11_USAGE_DEFAULT;
            td.BindFlags        = D3D11_BIND_SHADER_RESOURCE;
            td.MiscFlags        = D3D11_RESOURCE_MISC_SHARED_NTHANDLE |
                                  D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
            HRESULT hr = device->CreateTexture2D(&td, nullptr, ring.tex[i].ReleaseAndGetAddressOf());
            if (FAILED(hr)) { err = Fmt("CreateTexture2D(shared) hr=0x%08x", (uint32_t)hr); return false; }

            hr = ring.tex[i].As(&ring.mutex[i]);
            if (FAILED(hr)) { err = "QI IDXGIKeyedMutex"; return false; }

            ComPtr<IDXGIResource1> res1;
            hr = ring.tex[i].As(&res1);
            if (FAILED(hr)) { err = "QI IDXGIResource1"; return false; }
            hr = res1->CreateSharedHandle(nullptr,
                                          DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
                                          nullptr, &ring.localHandle[i]);
            if (FAILED(hr)) { err = Fmt("CreateSharedHandle hr=0x%08x", (uint32_t)hr); return false; }
        }

        // Privates Compose-Intermediate (identisch zum alten Layer-Intermediate).
        D3D11_TEXTURE2D_DESC td{};
        td.Width            = w;
        td.Height           = h;
        td.MipLevels        = 1;
        td.ArraySize        = 1;
        td.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage            = D3D11_USAGE_DEFAULT;
        td.BindFlags        = D3D11_BIND_UNORDERED_ACCESS;
        HRESULT hr = device->CreateTexture2D(&td, nullptr, ring.intermediate.ReleaseAndGetAddressOf());
        if (FAILED(hr)) { err = Fmt("CreateTexture2D(intermediate) hr=0x%08x", (uint32_t)hr); return false; }
        D3D11_UNORDERED_ACCESS_VIEW_DESC uavd{};
        uavd.Format        = DXGI_FORMAT_R8G8B8A8_UNORM;
        uavd.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
        hr = device->CreateUnorderedAccessView(ring.intermediate.Get(), &uavd,
                                               ring.intermediateUAV.ReleaseAndGetAddressOf());
        if (FAILED(hr)) { err = Fmt("CreateUAV(intermediate) hr=0x%08x", (uint32_t)hr); return false; }

        ring.width = w;
        ring.height = h;
        return true;
    }

    // Dupliziert die Ring- + Fence-Handles in den Zielprozess (Layer/iRacing) und
    // trägt die dort gültigen Werte in g_texOut ein. Alte Ziel-Handles schließt der
    // Layer beim Epoch-/Pid-Wechsel selbst; ein paar verwaiste Handles bei
    // Layer-Neustart sind unkritisch (Prozess-Exit räumt auf).
    bool DuplicateIntoTarget(const SharedRing& ring, HANDLE fenceLocalHandle, uint32_t targetPid) {
        HANDLE target = OpenProcess(PROCESS_DUP_HANDLE, FALSE, targetPid);
        if (!target) {
            Log(Fmt("DuplicateIntoTarget: OpenProcess(pid=%lu) err=%lu", targetPid, GetLastError()));
            return false;
        }
        bool ok = true;
        for (uint32_t i = 0; i < kTexRingBuffers && ok; ++i) {
            HANDLE dup = nullptr;
            ok = DuplicateHandle(GetCurrentProcess(), ring.localHandle[i], target, &dup, 0,
                                 FALSE, DUPLICATE_SAME_ACCESS) != 0;
            g_texOut.texHandleInTarget[i] = (uint64_t)(uintptr_t)dup;
        }
        if (ok) {
            HANDLE dup = nullptr;
            ok = DuplicateHandle(GetCurrentProcess(), fenceLocalHandle, target, &dup, 0,
                                 FALSE, DUPLICATE_SAME_ACCESS) != 0;
            g_texOut.fenceHandleInTarget = (uint64_t)(uintptr_t)dup;
        }
        CloseHandle(target);
        if (!ok) Log(Fmt("DuplicateIntoTarget: DuplicateHandle err=%lu", GetLastError()));
        g_texOut.targetPid = ok ? targetPid : 0;
        return ok;
    }

    // ---------------- BMP-Dump (--dump): Beweisfoto ohne VR ----------------------------

    bool DumpTexToBmp(ID3D11Device* device, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex,
                      const std::filesystem::path& path) {
        D3D11_TEXTURE2D_DESC desc{};
        tex->GetDesc(&desc);
        D3D11_TEXTURE2D_DESC sd = desc;
        sd.Usage = D3D11_USAGE_STAGING;
        sd.BindFlags = 0;
        sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        sd.MiscFlags = 0;
        ComPtr<ID3D11Texture2D> staging;
        if (FAILED(device->CreateTexture2D(&sd, nullptr, staging.ReleaseAndGetAddressOf())))
            return false;
        ctx->CopyResource(staging.Get(), tex);
        D3D11_MAPPED_SUBRESOURCE map{};
        if (FAILED(ctx->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &map))) return false;

        const uint32_t w = desc.Width, h = desc.Height;
        BITMAPFILEHEADER bfh{};
        BITMAPINFOHEADER bih{};
        bfh.bfType = 0x4D42;
        bfh.bfOffBits = sizeof(bfh) + sizeof(bih);
        bfh.bfSize = bfh.bfOffBits + w * h * 4;
        bih.biSize = sizeof(bih);
        bih.biWidth = (LONG)w;
        bih.biHeight = (LONG)h;
        bih.biPlanes = 1;
        bih.biBitCount = 32;
        bih.biCompression = BI_RGB;

        std::ofstream f(path, std::ios::binary);
        if (!f.is_open()) {
            ctx->Unmap(staging.Get(), 0);
            return false;
        }
        f.write(reinterpret_cast<const char*>(&bfh), sizeof(bfh));
        f.write(reinterpret_cast<const char*>(&bih), sizeof(bih));
        std::vector<uint8_t> row(w * 4);
        for (int32_t y = (int32_t)h - 1; y >= 0; --y) {
            const uint8_t* src = static_cast<const uint8_t*>(map.pData) + (size_t)y * map.RowPitch;
            for (uint32_t x = 0; x < w; ++x) {
                row[x * 4 + 0] = src[x * 4 + 2]; // RGBA → BGRA
                row[x * 4 + 1] = src[x * 4 + 1];
                row[x * 4 + 2] = src[x * 4 + 0];
                row[x * 4 + 3] = src[x * 4 + 3];
            }
            f.write(reinterpret_cast<const char*>(row.data()), row.size());
        }
        ctx->Unmap(staging.Get(), 0);
        return true;
    }

    // --hl-test: capture-host schreibt selbst einen HL-Block (nur Test, sonst ist der
    // Layer der Writer) — damit lässt sich der Hover-Border ohne VR verifizieren.
    HANDLE g_hlTestMapping = nullptr;
    uint8_t* g_hlTestView = nullptr;
    bool WriteHlTest(const char* id, uint64_t gen) {
        if (!g_hlTestView) {
            g_hlTestMapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                                 0, sizeof(HighlightSlot), kHighlightMappingName);
            if (!g_hlTestMapping) return false;
            g_hlTestView = static_cast<uint8_t*>(
                MapViewOfFile(g_hlTestMapping, FILE_MAP_WRITE, 0, 0, sizeof(HighlightSlot)));
            if (!g_hlTestView) return false;
        }
        HighlightSlot hl{};
        std::strncpy(hl.hoveredId, id, 15);
        hl.generation = gen;
        // Test-Modus: eigene PID als Duplikations-Ziel → übt den kompletten
        // DuplicateIntoTarget-Pfad ohne iRacing (Selbst-Duplikation ist legal).
        hl.layerPid = GetCurrentProcessId();
        std::memcpy(g_hlTestView, &hl, sizeof(hl));
        return true;
    }

} // namespace

int main(int argc, char** argv) {
    InitLog();

    bool dumpRequested = false;
    const char* hlTestId = nullptr;
    uint32_t parentPid = 0;
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--dump") dumpRequested = true;
        else if (a == "--hl-test" && i + 1 < argc) hlTestId = argv[++i];
        else if (a.rfind("--parent-pid=", 0) == 0) parentPid = (uint32_t)std::atoi(a.c_str() + 13);
    }

    Log(Fmt("capture-host startet (pid=%lu)%s%s parentPid=%lu", GetCurrentProcessId(),
            dumpRequested ? " [--dump]" : "", hlTestId ? " [--hl-test]" : "", parentPid));

    // Phase 4 (8.7.2026): Parent-Watch — stirbt die WPF (die uns gestartet hat),
    // beenden wir uns sofort mit (Gürtel + Hosenträger neben dem Atlas-tot-Exit;
    // deckt auch WPF-Crashes ab, bei denen OnExit/Stop nie läuft).
    if (parentPid != 0) {
        if (HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentPid)) {
            CreateThread(nullptr, 0,
                         [](LPVOID h) -> DWORD {
                             WaitForSingleObject((HANDLE)h, INFINITE);
                             // Log ist thread-safe (Mutex); danach hart raus.
                             Log("Parent-Prozess (WPF) beendet -- Self-Exit");
                             ExitProcess(0);
                         },
                         parent, 0, nullptr);
        } else {
            Log(Fmt("Parent-Watch: OpenProcess(pid=%lu) err=%lu -- Watch inaktiv",
                    parentPid, GetLastError()));
        }
    }

    winrt::init_apartment(winrt::apartment_type::multi_threaded);

    // Eigenes Device — die WDDM-Allokationen der Capture gehören damit DIESEM Prozess.
    // FL 11.1+ für ID3D11Device5/Fence. BGRA für WinRT-Interop.
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> ctx;
    {
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1};
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 1,
                                       D3D11_SDK_VERSION, device.ReleaseAndGetAddressOf(),
                                       nullptr, ctx.ReleaseAndGetAddressOf());
        if (FAILED(hr)) {
            Log(Fmt("FATAL: D3D11CreateDevice hr=0x%08x", (uint32_t)hr));
            return 1;
        }
    }
    ComPtr<ID3D11Device5> device5;
    ComPtr<ID3D11DeviceContext4> ctx4;
    if (FAILED(device.As(&device5)) || FAILED(ctx.As(&ctx4))) {
        Log("FATAL: ID3D11Device5/DeviceContext4 nicht verfuegbar");
        return 1;
    }
    Log("D3D11-Device erstellt (eigener Prozess, FL 11.1, Device5/Context4 ok)");

    // Shared Fence — EINER für die Prozess-Lebensdauer, monoton, resize-unabhängig.
    ComPtr<ID3D11Fence> fence;
    HANDLE fenceLocalHandle = nullptr;
    {
        HRESULT hr = device5->CreateFence(0, D3D11_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence));
        if (SUCCEEDED(hr))
            hr = fence->CreateSharedHandle(nullptr, GENERIC_ALL, nullptr, &fenceLocalHandle);
        if (FAILED(hr)) {
            Log(Fmt("FATAL: Shared Fence hr=0x%08x", (uint32_t)hr));
            return 1;
        }
    }
    uint64_t fenceCounter = 0;

    capturehost::ComposePipeline compose;
    {
        std::string err;
        if (!compose.init(device.Get(), err)) {
            Log("FATAL: Compose-Pipeline: " + err);
            return 1;
        }
    }
    Log("Compose-Pipeline bereit (CS kompiliert)");

    if (!CreateTexOutShm()) {
        Log(Fmt("FATAL: TexOut-SHM err=%lu", GetLastError()));
        return 1;
    }
    g_texOut.helperPid = GetCurrentProcessId();
    g_texOut.bufferCount = kTexRingBuffers;

    // Auf Atlas warten: SHM kann noch fehlen (WPF startet Helper + Atlas beim Connect).
    while (!OpenFrameShm()) {
        Log("FrameSlot-SHM noch nicht da -- Atlas laeuft noch nicht, retry in 500 ms");
        Sleep(500);
    }
    Log("FrameSlot-SHM offen (read-only)");

    // C/C2-Drossel-Env — identische Semantik wie bisher im Layer (zieht mit D1 hierher um).
    int captureHz = 30;
    if (const char* e = std::getenv("BEEHIVE_CAPTURE_HZ")) captureHz = std::atoi(e);
    const int64_t minPullNs = (captureHz > 0) ? (1000000000LL / captureHz) : 0;
    int dwmHz = captureHz;
    if (const char* e = std::getenv("BEEHIVE_DWM_CAPTURE_HZ")) dwmHz = std::atoi(e);
    const int64_t minUpdateNs = (dwmHz > 0) ? (1000000000LL / dwmHz) : 0;
    int composeHz = 60;
    if (const char* e = std::getenv("BEEHIVE_COMPOSE_HZ")) composeHz = std::atoi(e);
    if (composeHz <= 0 || composeHz > 240) composeHz = 60;
    Log(Fmt("C: Capture-Throttle=%d Hz, C2: DWM-MinUpdateInterval=%d Hz, Compose-Tick=%d Hz",
            captureHz, dwmHz, composeHz));

    std::unique_ptr<openxr_api_layer::capture::ICaptureWindow> capture;
    HWND attachedHwnd = nullptr;
    uint32_t attachedPid = 0;

    auto tryAttach = [&](HWND hwnd, uint32_t pid) {
        capture.reset();
        compose.invalidateSourceCache();
        try {
            capture = std::make_unique<openxr_api_layer::capture::CaptureWindowWinRT>(
                device.Get(), hwnd);
            capture->setMinPullIntervalNs(minPullNs);
            if (!capture->setMinUpdateIntervalNs(minUpdateNs))
                Log("C2: MinUpdateInterval nicht verfuegbar (Windows < 24H2) -- DWM-Drossel aus");
            attachedHwnd = hwnd;
            attachedPid = pid;
            Log(Fmt("WGC attached: hwnd=0x%llx atlasPid=%lu", (uint64_t)(uintptr_t)hwnd, pid));
            return true;
        } catch (const winrt::hresult_error& e) {
            Log(Fmt("WGC attach FEHLER hr=0x%08x (%s) -- retry naechster Tick",
                    (uint32_t)e.code().value, winrt::to_string(e.message()).c_str()));
            attachedHwnd = nullptr;
            attachedPid = 0;
            return false;
        }
    };

    HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr,
                                          CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                          TIMER_ALL_ACCESS);
    LARGE_INTEGER due{};
    due.QuadPart = -1;
    SetWaitableTimer(timer, &due, 1000 / composeHz, nullptr, nullptr, FALSE);

    SharedRing ring;
    uint64_t lastSeenGen = 0;
    ULONGLONG lastGenChangeMs = GetTickCount64();
    ULONGLONG lastStaleLogMs = 0;
    bool atlasWasLive = false;
    constexpr ULONGLONG kAtlasStaleExitMs = 10000;

    uint64_t ticks = 0, composes = 0;
    bool dumpDone = false;
    ULONGLONG lastStatsMs = GetTickCount64();
    ULONGLONG lastHeartbeatMs = GetTickCount64();
    uint32_t duplicatedForPid = 0;
    uint32_t duplicatedEpoch = 0;
    uint32_t dupFailedPid = 0;
    uint32_t dupFailedEpoch = 0;

    // Skip-Tick-Zustand: nur neu komponieren wenn sich compose-relevanter Input
    // geändert hat (Surface-Pointer, Quad-Payload, HL-Payload — NICHT die
    // Heartbeat-Generationen, die bumpen auch ohne Inhaltseänderung).
    ID3D11Texture2D* lastComposedSurface = nullptr;
    std::vector<uint8_t> lastComposeKey;

    // D2: Input-Quelle (0=unbestimmt, 1=OSR/AtlasTexIn, 2=WGC/Fenster). Bei OSR halten
    // wir die aktuelle Shared-Textur (osrTex) stabil bis ein neuer frameCounter kommt —
    // so bleiben Ring-Sizing + Skip-Tick (Surface-Pointer-Vergleich) unverändert.
    int sourceMode = 0;
    ComPtr<ID3D11Texture2D> osrTex;
    uint64_t osrFrame = 0;

    for (;;) {
        WaitForSingleObject(timer, 1000);
        ++ticks;
        const ULONGLONG nowMs = GetTickCount64();

        if (hlTestId) WriteHlTest(hlTestId, ticks);

        FrameSlot frame{};
        QuadSlot slots[kMaxQuads]{};
        if (!ReadFrameStable(frame, slots)) continue;

        // Liveness: Atlas-Heartbeat bumpt generation alle 100 ms. Self-Exit aber NUR,
        // wenn der Atlas-PROZESS wirklich tot ist — der Heartbeat allein ist kein
        // verlässliches Signal: iRacings Lade-Phase sättigt alle Kerne und hungert
        // Electrons Timer >10 s aus (Fehl-Exit beim ersten Test 8.7., gleiche Falle
        // wie die F5-Watchdog-Fehltrips 16.6. durch Background-Throttling).
        if (frame.generation != lastSeenGen) {
            lastSeenGen = frame.generation;
            lastGenChangeMs = nowMs;
            if (frame.hwnd != 0) atlasWasLive = true;
        } else if (atlasWasLive && (nowMs - lastGenChangeMs) > kAtlasStaleExitMs) {
            bool atlasProcessAlive = false;
            if (HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE,
                                       frame.producerPid)) {
                DWORD code = 0;
                atlasProcessAlive = GetExitCodeProcess(p, &code) && code == STILL_ACTIVE;
                CloseHandle(p);
            }
            if (!atlasProcessAlive) {
                Log(Fmt("Atlas tot (Heartbeat >%llu ms stale, pid=%lu weg) -- Self-Exit",
                        kAtlasStaleExitMs, frame.producerPid));
                break;
            }
            // Prozess lebt, nur ausgehungert (typisch: iRacing-Load) — weiter warten.
            if (nowMs - lastStaleLogMs > 10000) {
                Log(Fmt("Atlas-Heartbeat stale, Prozess pid=%lu lebt aber -- warte (Load-Phase?)",
                        frame.producerPid));
                lastStaleLogMs = nowMs;
            }
        }

        // Input-Quelle einmalig festlegen: OSR (AtlasTexIn-SHM) bevorzugt, sonst WGC.
        if (sourceMode == 0) {
            if (TryOpenAtlasTexInShm()) {
                AtlasTexIn probe{};
                if (ReadAtlasTexInStable(probe) && probe.frameCounter != 0 && probe.atlasPid != 0) {
                    sourceMode = 1;
                    Log("Input-Modus: OSR (Atlas useSharedTexture, AtlasTexIn-SHM)");
                }
            }
            if (sourceMode == 0 && frame.hwnd != 0) {
                sourceMode = 2;
                Log("Input-Modus: WGC (Atlas-Fenster)");
            }
            if (sourceMode == 0) continue; // Atlas hat noch nichts publiziert
        }

        ID3D11Texture2D* surface = nullptr;
        if (sourceMode == 1) {
            // OSR: neuen Frame aus AtlasTexIn öffnen; aktuelle Textur halten (stabil bis
            // neuer frameCounter). consumed = frameCounter-1 → Atlas gibt Ältere frei,
            // hält die aktuelle → Content bleibt gültig für Recompose-auf-Key-Change.
            AtlasTexIn ati{};
            if (ReadAtlasTexInStable(ati) && ati.atlasPid != 0 && ati.frameCounter != 0 &&
                ati.frameCounter != osrFrame) {
                ComPtr<ID3D11Texture2D> t;
                if (OpenAtlasTexture(device5.Get(), ati, t)) {
                    osrTex = std::move(t);
                    osrFrame = ati.frameCounter;
                    WriteConsumed(ati.frameCounter - 1);
                }
            }
            surface = osrTex.Get();
            if (!surface) continue; // noch kein Frame geöffnet
        } else {
            // WGC (bestehend): Re-Attach bei erstem hwnd oder Atlas-Restart (hwnd/pid-Wechsel).
            const HWND wantHwnd = (HWND)(uintptr_t)frame.hwnd;
            if (wantHwnd != nullptr && frame.producerPid != 0 &&
                (wantHwnd != attachedHwnd || frame.producerPid != attachedPid)) {
                if (attachedHwnd)
                    Log(Fmt("Atlas-Wechsel erkannt (hwnd 0x%llx->0x%llx, pid %lu->%lu) -- Re-Attach",
                            (uint64_t)(uintptr_t)attachedHwnd, frame.hwnd, attachedPid,
                            frame.producerPid));
                tryAttach(wantHwnd, frame.producerPid);
            }
            if (!capture) continue;

            try {
                surface = capture->getSurface();
            } catch (const winrt::hresult_error& e) {
                Log(Fmt("getSurface FEHLER hr=0x%08x -- Capture-Reset", (uint32_t)e.code().value));
                capture.reset();
                attachedHwnd = nullptr;
                continue;
            }
            if (!surface) continue;
        }

        // Ring an WGC-Surface-Größe koppeln (Atlas-Resize → neue Surface-Größe →
        // texEpoch++ + neue Handles; der Layer erkennt den Epoch-Wechsel und
        // re-öffnet). Fence bleibt derselbe.
        D3D11_TEXTURE2D_DESC sdesc{};
        surface->GetDesc(&sdesc);
        if (sdesc.Width != ring.width || sdesc.Height != ring.height) {
            std::string err;
            if (!CreateRing(device.Get(), sdesc.Width, sdesc.Height, ring, err)) {
                Log("Ring-Erzeugung FEHLER: " + err);
                Sleep(500);
                continue;
            }
            g_texOut.texEpoch++;
            g_texOut.texWidth = ring.width;
            g_texOut.texHeight = ring.height;
            g_texOut.latestIndex = 0;
            for (uint32_t i = 0; i < kTexRingBuffers; ++i) g_texOut.fenceValue[i] = 0;
            duplicatedForPid = 0; // neue Handles → Duplikation neu
            lastComposedSurface = nullptr;
            Log(Fmt("Ring neu: %ux%u epoch=%u", ring.width, ring.height, g_texOut.texEpoch));
        }

        // HL-Block (Layer schreibt ihn; fehlt er, Defaults).
        TryOpenHlShm();
        HighlightSlot hl{};
        ReadHlStable(hl);

        // Handle-Duplikation in den Zielprozess, sobald layerPid bekannt (oder bei
        // Epoch-/Pid-Wechsel erneut). Fehlschläge (z.B. tote PID im HL-Block nach
        // iRacing-Exit → OpenProcess err=87) NICHT im Tick-Takt wiederholen — die
        // (pid,epoch)-Kombination merken und erst bei Wechsel neu versuchen
        // (Log-Spam-Bug 11.7.).
        if (hl.layerPid != 0 &&
            (hl.layerPid != duplicatedForPid || g_texOut.texEpoch != duplicatedEpoch) &&
            (hl.layerPid != dupFailedPid || g_texOut.texEpoch != dupFailedEpoch)) {
            if (DuplicateIntoTarget(ring, fenceLocalHandle, hl.layerPid)) {
                duplicatedForPid = hl.layerPid;
                duplicatedEpoch = g_texOut.texEpoch;
                Log(Fmt("Handles dupliziert in Zielprozess pid=%lu (epoch=%u)",
                        hl.layerPid, g_texOut.texEpoch));
            } else {
                dupFailedPid = hl.layerPid;
                dupFailedEpoch = g_texOut.texEpoch;
            }
        }

        // Skip-Tick: compose-relevanter Input unverändert → nur Heartbeat.
        std::vector<uint8_t> key(sizeof(uint32_t) + sizeof(slots) + 40);
        {
            uint8_t* p = key.data();
            std::memcpy(p, &frame.placeModeOn, sizeof(uint32_t)); p += sizeof(uint32_t);
            std::memcpy(p, slots, sizeof(slots)); p += sizeof(slots);
            std::memcpy(p, hl.hoveredId, 16); p += 16;
            std::memcpy(p, hl.grabbedId, 16); p += 16;
            std::memcpy(p, &hl.dragOpacity, 4); p += 4;
            std::memcpy(p, &hl.flags, 4);
        }
        const bool inputChanged = (surface != lastComposedSurface) || (key != lastComposeKey);

        if (inputChanged) {
            const uint32_t j = (g_texOut.latestIndex + 1) % kTexRingBuffers;
            const uint32_t n = (frame.quadCount < kMaxQuads) ? frame.quadCount : kMaxQuads;
            // Compose ins private Intermediate, dann unter Keyed-Mutex (non-blocking!)
            // in den Shared-Buffer kopieren. AcquireSync(0,0) schlägt nur fehl, wenn
            // der Layer gerade genau diesen Buffer kopiert — dann diesen Tick skippen
            // (bei 3 Buffern praktisch nie: Layer fasst nur latest/lastGood an).
            compose.run(ctx.Get(), surface, ring.intermediateUAV.Get(), frame, slots, n, hl,
                        ring.width, ring.height);
            if (ring.mutex[j]->AcquireSync(0, 0) == S_OK) {
                ctx->CopyResource(ring.tex[j].Get(), ring.intermediate.Get());
                ring.mutex[j]->ReleaseSync(0);
                ctx4->Signal(fence.Get(), ++fenceCounter);
                g_texOut.fenceValue[j] = fenceCounter;
                g_texOut.latestIndex = j;
                g_texOut.frameCounter++;
                PublishTexOut();
                lastHeartbeatMs = nowMs;
                lastComposedSurface = surface;
                lastComposeKey = std::move(key);
                ++composes;
            }
        } else if (nowMs - lastHeartbeatMs >= 100) {
            // Heartbeat ohne Inhalts-Update: Layer-Liveness-Check bleibt grün,
            // latestIndex/fenceValue unangetastet.
            PublishTexOut();
            lastHeartbeatMs = nowMs;
        }

        // --dump: nach ~2 s ein Beweisfoto des zuletzt KOMPONIERTEN Buffers.
        if (dumpRequested && !dumpDone && composes > 0 && ticks > (uint64_t)composeHz * 2) {
            auto dumpPath = g_logPath.parent_path() / "capture_dump.bmp";
            if (DumpTexToBmp(device.Get(), ctx.Get(), ring.tex[g_texOut.latestIndex].Get(), dumpPath))
                Log(Fmt("--dump geschrieben (komponierter Buffer %u): %s",
                        g_texOut.latestIndex, dumpPath.string().c_str()));
            else
                Log("--dump FEHLGESCHLAGEN (Staging/Map)");
            dumpDone = true;
        }

        if (nowMs - lastStatsMs >= 5000) {
            Log(Fmt("stats: ticks=%llu composes=%llu ring=%ux%u epoch=%u latest=%u fence=%llu "
                    "completed=%llu quads=%u",
                    ticks, composes, ring.width, ring.height, g_texOut.texEpoch,
                    g_texOut.latestIndex, fenceCounter, fence->GetCompletedValue(),
                    frame.quadCount));
            lastStatsMs = nowMs;
        }
    }

    CloseHandle(timer);
    capture.reset();
    ring.reset();
    if (fenceLocalHandle) CloseHandle(fenceLocalHandle);
    if (g_frameView) UnmapViewOfFile(g_frameView);
    if (g_frameMapping) CloseHandle(g_frameMapping);
    if (g_hlView) UnmapViewOfFile(g_hlView);
    if (g_hlMapping) CloseHandle(g_hlMapping);
    if (g_texOutView) UnmapViewOfFile(g_texOutView);
    if (g_texOutMapping) CloseHandle(g_texOutMapping);
    Log("capture-host beendet");
    return 0;
}
