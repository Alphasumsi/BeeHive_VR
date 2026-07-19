// Gemeinsame SHM-Binär-Layouts + Objekt-Namen für Atlas (Electron/koffi),
// OpenXR-Layer (xr-api-beehive) und capture-host.
//
// Writer-Zuordnung (je Block genau EIN Writer):
//   Local\BeeHiveVR_Frame    → Atlas       (FrameSlot + kMaxQuads×QuadSlot, 1008 B)
//   Local\BeeHiveVR_PlaceOut → Layer       (Drag/Hover-Feedback an Atlas, 96 B, nicht hier definiert)
//   Local\BeeHiveVR_TexOut   → capture-host (Shared-Texture-Ring-Publikation, 256 B)
//   Local\BeeHiveVR_HL       → Layer       (Highlight-Zustand für den Helper-Compose, 64 B)
//
// Seqlock-Konvention (alle Blöcke): Writer schreibt Payload, MemoryBarrier, dann EIN
// generation++-Store. Reader liest den ganzen Block zweimal (memcpy) und akzeptiert nur
// bei identischer generation (Tearing-Schutz, vgl. Recenter-Fix 10.6.2026).
//
// ⚠ Layouts müssen byte-identisch zu app\src\ipc\shared-frame.ts (koffi) und zu den
// (bis zur Cleanup-Phase noch privaten) Kopien in layer.cpp bleiben.

#pragma once

#include <cstdint>

namespace beehive::shm {

    inline constexpr wchar_t kFrameMappingName[]     = L"Local\\BeeHiveVR_Frame";
    inline constexpr wchar_t kTexOutMappingName[]    = L"Local\\BeeHiveVR_TexOut";
    inline constexpr wchar_t kHighlightMappingName[] = L"Local\\BeeHiveVR_HL";
    inline constexpr wchar_t kAtlasTexInMappingName[]= L"Local\\BeeHiveVR_AtlasTexIn";

    inline constexpr uint32_t kMaxQuads = 12;

    // pixelFormat-Enum für AtlasTexIn (Electron textureInfo.pixelFormat).
    inline constexpr uint32_t kPixBgra    = 0;  // Windows-OSR-Default (Phase 0 bestätigt)
    inline constexpr uint32_t kPixRgba    = 1;
    inline constexpr uint32_t kPixRgbaF16 = 2;

    // ---- Atlas → (Layer + Helper) ------------------------------------------------

    struct FrameSlot {
        uint64_t generation;    // @0  Heartbeat, Atlas bumpt alle 100 ms
        uint32_t producerPid;   // @8  Atlas-PID (Restart-Erkennung)
        uint32_t placeModeOn;   // @12 Place-in-VR-Gate (0/1)
        uint64_t hwnd;          // @16 BrowserWindow-HWND (WGC-Capture-Ziel)
        uint32_t width;         // @24 Atlas-Textur-Breite (px)
        uint32_t height;        // @28 Atlas-Textur-Höhe (px)
        uint32_t format;        // @32 DXGI_FORMAT (informativ)
        uint32_t quadCount;     // @36 aktive QuadSlots
        uint32_t recenterEpoch; // @40 B7-Recenter-Trigger (monoton)
        uint32_t reserved2;     // @44 Padding auf 48
    };
    static_assert(sizeof(FrameSlot) == 48, "FrameSlot muss 48 Bytes sein (koffi-Kontrakt)");

    struct QuadSlot {
        char     id[16];        // @0  Quad-Id (nicht zwingend NUL-terminiert)
        uint32_t rectX, rectY;  // @16 Atlas-Subrect-Offset (px)
        uint32_t rectW, rectH;  // @24 Atlas-Subrect-Größe (px)
        float    posX, posY, posZ;            // @32 Welt-Position (LOCAL-Space, m)
        float    quatX, quatY, quatZ, quatW;  // @44 Orientierung (Unit-Quat)
        float    sizeW, sizeH;  // @60 Quad-Größe (m)
        uint32_t visible;       // @68 0/1
        float    opacity;       // @72 B10, 0..1
        float    bgOpacity;     // @76 CSS-BG-Opacity (nur Drag-Init)
    };
    static_assert(sizeof(QuadSlot) == 80, "QuadSlot muss 80 Bytes sein (koffi-Kontrakt)");

    inline constexpr uint32_t kFrameMappingSize = sizeof(FrameSlot) + kMaxQuads * sizeof(QuadSlot); // 1008

    // ---- capture-host → Layer ----------------------------------------------------
    // D1 (8.7.2026): Der Helper komponiert in einen 3er-Ring aus Shared-Texturen und
    // publiziert hier, welcher Buffer wann (Fence-Wert) fertig ist. Handle-Werte sind
    // bereits per DuplicateHandle IN DEN ZIELPROZESS (targetPid) dupliziert — der Layer
    // macht selbst KEIN OpenProcess (crasht aus iRacings Adressraum, Lehre 17.6.).
    // texEpoch bumpt bei jedem Ring-Neuaufbau (Atlas-Resize); 0 = noch keine Texturen.

    inline constexpr uint32_t kTexRingBuffers = 3;

    struct TexOutSlot {
        uint64_t generation;                        // @0   Seqlock + 100ms-Heartbeat
        uint32_t helperPid;                         // @8   capture-host-PID (Restart-Erkennung)
        uint32_t texEpoch;                          // @12  Ring-Generation (0 = keine)
        uint32_t texWidth;                          // @16
        uint32_t texHeight;                         // @20
        uint32_t bufferCount;                       // @24  = kTexRingBuffers
        uint32_t latestIndex;                       // @28  zuletzt signalisierter Buffer
        uint64_t fenceValue[kTexRingBuffers];       // @32  Fence-Wert je Buffer (fertig wenn completed >= Wert)
        uint64_t texHandleInTarget[kTexRingBuffers];// @56  im Zielprozess gültige NT-Handle-Werte
        uint64_t fenceHandleInTarget;               // @80  dito für den Shared Fence
        uint32_t targetPid;                         // @88  PID, für die die Handles gelten (Layer verifiziert!)
        uint32_t pad0;                              // @92
        uint64_t frameCounter;                      // @96  Diagnose
        uint32_t reserved[38];                      // @104 → 256
    };
    static_assert(sizeof(TexOutSlot) == 256, "TexOutSlot muss 256 Bytes sein");

    // ---- Layer → capture-host ----------------------------------------------------
    // Highlight-/Interaktions-Zustand ist Layer-eigen (Ray-Hit-Test, Grab), wird aber
    // seit D1 im Helper in die Border-Konstanten gefaltet. layerPid = iRacing-PID —
    // Ziel für die Handle-Duplikation (s.o.). flags bit0 = dragOpacity gültig (Grab aktiv).

    struct HighlightSlot {
        uint64_t generation;    // @0
        char     hoveredId[16]; // @8   leer = kein Hover
        char     grabbedId[16]; // @24  leer = kein Grab
        float    dragOpacity;   // @40  Live-Override während ALT-Drag
        uint32_t flags;         // @44  bit0 = dragOpacity gültig
        uint32_t layerPid;      // @48  = iRacing-PID (Handle-Duplikations-Ziel)
        uint32_t reserved[3];   // @52  → 64
    };
    static_assert(sizeof(HighlightSlot) == 64, "HighlightSlot muss 64 Bytes sein");

    // ---- Atlas → capture-host (D2, OSR-Shared-Texture) ---------------------------
    // D2 (14.7.2026): Statt WGC-Capture des Atlas-FENSTERS bekommt der capture-host
    // die gerenderte Textur direkt aus Chromiums OSR-Pfad (useSharedTexture). Der
    // Atlas publiziert pro paint den prozess-LOKALEN NT-Handle-Wert der Shared-Textur
    // (Electron textureInfo.handle.ntHandle, 8 B) + Metadaten; der capture-host zieht
    // ihn per DuplicateHandle(atlasProc→self) + OpenSharedResource1 (Richtung wg.
    // [[project_no_process_enum_in_layer]] unkritisch — capture-host ist NICHT im
    // iRacing-Adressraum). KEIN Keyed-Mutex bei bgra/rgba → Sync = „copy+release ASAP":
    // der capture-host kopiert sofort in seinen Ring und meldet consumedFrameCounter
    // zurück; erst dann gibt der Atlas die Electron-Textur frei (10er-Frame-Pool).
    //
    // Writer = Atlas (Seqlock über generation) für ALLE Felder AUSSER
    // consumedFrameCounter — das schreibt ausschließlich der capture-host (einzelner
    // 8-Byte-Store, 8-Byte-aligned → atomar auf x64; kein Seqlock nötig, Atlas liest
    // nur monoton steigende Werte).

    struct AtlasTexIn {
        uint64_t generation;          // @0   Seqlock (Atlas) + Heartbeat
        uint32_t atlasPid;            // @8   Atlas-PID (DuplicateHandle-Quelle + Restart)
        uint32_t pixelFormat;         // @12  kPixBgra|kPixRgba|kPixRgbaF16
        uint64_t ntHandle;            // @16  NT-Handle-WERT im Atlas-Prozess (Buffer→u64)
        uint64_t frameCounter;        // @24  monoton, +1 je publiziertem paint
        uint32_t codedWidth;          // @32  Textur-Dimension (codedSize)
        uint32_t codedHeight;         // @36
        uint32_t visRectX;            // @40  visibleRect (OSR: i.d.R. voll, 0/0)
        uint32_t visRectY;            // @44
        uint32_t visRectW;            // @48
        uint32_t visRectH;            // @52
        uint64_t consumedFrameCounter;// @56  capture-host = Writer: zuletzt kopierter frameCounter
        uint32_t reserved[16];        // @64  → 128
    };
    static_assert(sizeof(AtlasTexIn) == 128, "AtlasTexIn muss 128 Bytes sein (koffi-Kontrakt)");

    inline constexpr uint32_t kAtlasTexInMappingSize = sizeof(AtlasTexIn); // 128

} // namespace beehive::shm
